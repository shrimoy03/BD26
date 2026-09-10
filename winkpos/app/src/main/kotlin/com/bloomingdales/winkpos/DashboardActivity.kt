package com.bloomingdales.winkpos

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.LayoutInflater
import android.view.View
import android.widget.Button
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.activity.addCallback
import androidx.appcompat.app.AppCompatActivity
import com.bloomingdales.winkpos.link.PosLink
import com.bloomingdales.winkpos.link.PosMessage
import java.util.Locale
import java.util.concurrent.Executors

/**
 * Post-check-in customer dashboard: greeting, Loyallist loyalty summary,
 * offers, and the order panel. Pay charges the customer's preferred saved
 * card by calling the Wink payments API directly (see WinkPaymentsClient).
 *
 * Two sale modes:
 *  - Standalone demo: the cart icon "scans" demo items and totals are local.
 *  - Register-driven: the merchant POS sent START_PAYMENT (PosLink.RegisterSale
 *    is pending) — the order panel shows the register's amount, Pay charges
 *    exactly that, and the outcome is reported back as PAYMENT_RESULT.
 */
class DashboardActivity : AppCompatActivity(), PosLink.Listener {

    data class DemoItem(val name: String, val detail: String, val priceCents: Int)

    private val demoItems = listOf(
        DemoItem("Valentino - Donna Born In Roma Eau de Parfum", "1 Count", 17_000),
        DemoItem("Diamond Stud Earrings (1/3 ct. t.w.) in 14k White Gold", "1 Count", 29_900),
    )

    private val cart = mutableListOf<DemoItem>()
    private var rewardApplied = false
    private var paying = false

    // One order id per cart state, reused across retries so the gateway can
    // dedupe if a timed-out attempt actually went through.
    private var currentOrderId: String? = null

    private val executor = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())

    private lateinit var itemsContainer: LinearLayout
    private lateinit var emptyCartHint: TextView
    private lateinit var subtotalValue: TextView
    private lateinit var taxesValue: TextView
    private lateinit var totalValue: TextView
    private lateinit var rewardRow: View
    private lateinit var cartBadge: TextView
    private lateinit var payButton: Button
    private lateinit var orderTitle: TextView
    private lateinit var pointsText: TextView

    /** Live Okta loyalty balance; null until fetched (or when unconfigured). */
    private var oktaPoints: Int? = null

    /** Register-driven sale mode: the merchant POS owns the amount. */
    private val registerMode: Boolean get() = PosLink.RegisterSale.isPending

    private val subtotalCents: Int get() = cart.sumOf { it.priceCents }
    private val rewardCents: Int get() = if (rewardApplied && subtotalCents > 0) 1_000 else 0
    private val taxCents: Int get() = Math.round((subtotalCents - rewardCents) * TAX_RATE).toInt()
    private val totalCents: Int
        get() = if (registerMode) {
            PosLink.RegisterSale.amountCents.toInt() // register total is final (tax included)
        } else {
            subtotalCents - rewardCents + taxCents
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_dashboard)
        enterKioskMode()

        itemsContainer = findViewById(R.id.itemsContainer)
        emptyCartHint = findViewById(R.id.emptyCartHint)
        subtotalValue = findViewById(R.id.subtotalValue)
        taxesValue = findViewById(R.id.taxesValue)
        totalValue = findViewById(R.id.totalValue)
        rewardRow = findViewById(R.id.rewardRow)
        cartBadge = findViewById(R.id.cartBadge)
        payButton = findViewById(R.id.payButton)
        orderTitle = findViewById(R.id.orderTitle)

        findViewById<TextView>(R.id.greetingText).text =
            getString(R.string.hello_name, CheckinSession.firstName.ifEmpty { "there" })
        pointsText = findViewById(R.id.pointsText)

        bindPreferredCard()
        fetchLoyaltyPoints()

        // Demo "scanner": tapping the cart rings up the next demo item.
        findViewById<ImageView>(R.id.cartIcon).setOnClickListener { scanNextItem() }

        findViewById<View>(R.id.signOutButton).setOnClickListener { signOut() }

        findViewById<Button>(R.id.rewardsRedeemButton).setOnClickListener {
            if (registerMode) {
                toast(getString(R.string.register_owns_sale))
            } else if (subtotalCents == 0) {
                toast(getString(R.string.reward_needs_items))
            } else if (!rewardApplied) {
                rewardApplied = true
                currentOrderId = null
                renderTotals()
                toast(getString(R.string.reward_applied))
            }
        }
        val offerToast = View.OnClickListener { toast(getString(R.string.offer_applied)) }
        findViewById<Button>(R.id.offer1Redeem).setOnClickListener(offerToast)
        findViewById<Button>(R.id.offer2Redeem).setOnClickListener(offerToast)
        findViewById<Button>(R.id.offer3Redeem).setOnClickListener(offerToast)

        payButton.setOnClickListener { pay() }

        // Swallow Back while a charge is in flight — leaving mid-payment could
        // hide a completed charge.
        onBackPressedDispatcher.addCallback(this) {
            if (!paying) {
                isEnabled = false
                onBackPressedDispatcher.onBackPressed()
                isEnabled = true
            }
        }

        renderItems()
        renderTotals()
    }

    /**
     * Loyalty points from the customer's Okta profile (user_metadata.points),
     * same source the NRF demo read. Shows "Loading…" until the balance
     * arrives; resolves to 0 when unconfigured, the user id is missing, or
     * the fetch fails.
     */
    private fun fetchLoyaltyPoints() {
        val userId = CheckinSession.oktaUserId
        if (!OktaRewardsClient.isConfigured || userId == null) {
            showPoints(0)
            return
        }
        executor.execute {
            val points = try {
                OktaRewardsClient.fetchPoints(userId)
            } catch (e: Exception) {
                android.util.Log.w("Dashboard", "Okta points fetch failed", e)
                0
            }
            mainHandler.post {
                if (isFinishing || isDestroyed) return@post
                oktaPoints = points
                showPoints(points)
            }
        }
    }

    private fun showPoints(points: Int) {
        pointsText.text = getString(
            R.string.points_dynamic,
            String.format(Locale.US, "%,d", points),
        )
        pointsText.setTextColor(
            androidx.core.content.ContextCompat.getColor(this, R.color.text_dark),
        )
        // Progress toward the next 1,000-point reward (1,000 pts = $10).
        findViewById<android.widget.ProgressBar>(R.id.pointsProgress)
            .progress = (points % 1_000) / 10
    }

    /** Award purchase points to the Okta profile — fire and forget. */
    private fun awardLoyaltyPoints() {
        val userId = CheckinSession.oktaUserId
        val current = oktaPoints
        if (!OktaRewardsClient.isConfigured || userId == null || current == null) return
        executor.execute {
            try {
                OktaRewardsClient.setPoints(
                    userId, current + OktaRewardsClient.POINTS_PER_PURCHASE,
                )
            } catch (e: Exception) {
                android.util.Log.w("Dashboard", "Okta points award failed", e)
            }
        }
    }

    private fun bindPreferredCard() {
        val card = CheckinSession.preferredCard
        findViewById<TextView>(R.id.cardLast4).text =
            card?.let { "#${it.last4}" } ?: getString(R.string.no_card_on_file)
    }

    override fun onResume() {
        super.onResume()
        PosLink.addListener(this)
        renderItems()
        renderTotals()
    }

    override fun onPause() {
        super.onPause()
        PosLink.removeListener(this)
    }

    // ----- Register link (merchant POS via Wi-Fi or USB/JPxSerialServer) -----

    override fun onStartPayment(orderId: String, amountCents: Long) {
        toast(getString(R.string.register_sale_started, money(amountCents.toInt())))
        renderItems()
        renderTotals()
    }

    override fun onCancelPayment(orderId: String?) {
        toast(getString(R.string.register_sale_cancelled))
        renderItems()
        renderTotals()
    }

    private fun scanNextItem() {
        if (registerMode) {
            toast(getString(R.string.register_owns_sale))
            return
        }
        if (cart.size >= demoItems.size) {
            toast(getString(R.string.all_items_scanned))
            return
        }
        cart += demoItems[cart.size]
        currentOrderId = null
        renderItems()
        renderTotals()
    }

    private fun renderItems() {
        itemsContainer.removeAllViews()
        val inflater = LayoutInflater.from(this)
        if (registerMode) {
            // Payment confirmation: the itemized sale lives on the register, so
            // this side is just the amount, the card (large, with the
            // customer's name on it), and Pay.
            emptyCartHint.visibility = View.GONE
            orderTitle.text = getString(R.string.order_summary)
            cartBadge.visibility = View.GONE
            findViewById<View>(R.id.subtotalRow).visibility = View.GONE
            findViewById<View>(R.id.taxesRow).visibility = View.GONE
            findViewById<View>(R.id.preferredRow).visibility = View.GONE
            findViewById<View>(R.id.smallCardRow).visibility = View.GONE
            findViewById<View>(R.id.bigCardBlock).visibility = View.VISIBLE
            findViewById<TextView>(R.id.bigCardAlias).text =
                CheckinSession.firstName.uppercase().ifEmpty { "LOYALLIST MEMBER" }
            findViewById<TextView>(R.id.bigCardNumber).text =
                CheckinSession.preferredCard?.let { "•••• ${it.last4}" } ?: ""
            return
        }
        findViewById<View>(R.id.subtotalRow).visibility = View.VISIBLE
        findViewById<View>(R.id.taxesRow).visibility = View.VISIBLE
        findViewById<View>(R.id.preferredRow).visibility = View.VISIBLE
        findViewById<View>(R.id.smallCardRow).visibility = View.VISIBLE
        findViewById<View>(R.id.bigCardBlock).visibility = View.GONE
        for (item in cart) {
            val row = inflater.inflate(R.layout.row_order_item, itemsContainer, false)
            row.findViewById<TextView>(R.id.itemName).text = item.name
            row.findViewById<TextView>(R.id.itemDetail).text = item.detail
            row.findViewById<TextView>(R.id.itemPrice).text = money(item.priceCents)
            itemsContainer.addView(row)
        }
        emptyCartHint.visibility = if (cart.isEmpty()) View.VISIBLE else View.GONE
        orderTitle.text = getString(
            if (cart.isEmpty()) R.string.your_items else R.string.order_summary,
        )
        cartBadge.visibility = if (cart.isEmpty()) View.GONE else View.VISIBLE
        cartBadge.text = cart.size.toString()
    }

    private fun renderTotals() {
        subtotalValue.text = money(if (registerMode) totalCents else subtotalCents)
        taxesValue.text = money(if (registerMode) 0 else taxCents)
        totalValue.text = money(totalCents)
        rewardRow.visibility = if (!registerMode && rewardCents > 0) View.VISIBLE else View.GONE

        val canPay = totalCents > 0 && CheckinSession.preferredCard != null && !paying
        payButton.isEnabled = canPay
        payButton.setBackgroundResource(
            if (canPay) R.drawable.bg_btn_black else R.drawable.bg_btn_disabled,
        )
    }

    private fun pay() {
        val card = CheckinSession.preferredCard ?: return
        if (paying || totalCents <= 0) return
        paying = true
        payButton.text = getString(R.string.processing)
        findViewById<View>(R.id.processingOverlay).visibility = View.VISIBLE
        renderTotals()

        val amount = totalCents
        val fromRegister = registerMode
        // Register sales reuse the register's order id so both systems (and
        // the gateway's retry dedupe) refer to the same order.
        val orderId = if (fromRegister) {
            PosLink.RegisterSale.orderId ?: return
        } else {
            currentOrderId ?: "BLM-${System.currentTimeMillis()}".also { currentOrderId = it }
        }

        executor.execute {
            try {
                val result = WinkPaymentsClient.purchase(
                    env = BuildConfig.WINK_ENV,
                    merchantClientSecret = BuildConfig.WINK_MERCHANT_CLIENT_SECRET,
                    winkCardToken = card.winkCardToken,
                    amountCents = amount,
                    currencyCode = "USD",
                    orderId = orderId,
                    description = "Bloomingdale's in-store purchase",
                )
                awardLoyaltyPoints()
                mainHandler.post {
                    if (isFinishing || isDestroyed) return@post
                    if (fromRegister) {
                        PosLink.sendResult(
                            PosMessage.STATUS_APPROVED,
                            method = "Wink",
                            token = card.winkCardToken,
                        )
                    }
                    startActivity(
                        ThankYouActivity.intent(this, result.transactionId, result.amountCents),
                    )
                    finish()
                }
            } catch (e: Exception) {
                mainHandler.post {
                    if (isFinishing || isDestroyed) return@post
                    if (fromRegister) {
                        PosLink.sendResult(
                            PosMessage.STATUS_DECLINED,
                            reason = e.message ?: getString(R.string.payment_failed),
                        )
                    }
                    paying = false
                    payButton.text = getString(R.string.pay)
                    findViewById<View>(R.id.processingOverlay).visibility = View.GONE
                    renderItems()
                    renderTotals()
                    toast(e.message ?: getString(R.string.payment_failed))
                }
            }
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) enterKioskMode()
    }

    private fun signOut() {
        // Don't leave the register hanging on an awaiting-terminal banner.
        // The reason lets the register tell a walk-away (void the sale and
        // start fresh) from a capture cancel (retry the same sale).
        if (registerMode) PosLink.sendResult(PosMessage.STATUS_CANCELLED, reason = "SIGNED_OUT")
        CheckinSession.clear()
        finish()
    }

    private fun toast(message: String) =
        Toast.makeText(this, message, Toast.LENGTH_LONG).show()

    private fun money(cents: Int): String =
        String.format(Locale.US, "$%,.2f", cents / 100.0)

    override fun onDestroy() {
        super.onDestroy()
        executor.shutdown()
    }

    companion object {
        private const val TAX_RATE = 0.0825

        fun intent(context: Context): Intent =
            Intent(context, DashboardActivity::class.java)
    }
}
