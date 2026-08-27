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
import java.util.Locale
import java.util.concurrent.Executors

/**
 * Post-check-in customer dashboard: greeting, Loyallist loyalty summary,
 * offers, and the order panel. Pay charges the customer's preferred saved
 * card by calling the Wink payments API directly (see WinkPaymentsClient).
 */
class DashboardActivity : AppCompatActivity() {

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

    private val subtotalCents: Int get() = cart.sumOf { it.priceCents }
    private val rewardCents: Int get() = if (rewardApplied && subtotalCents > 0) 1_000 else 0
    private val taxCents: Int get() = Math.round((subtotalCents - rewardCents) * TAX_RATE).toInt()
    private val totalCents: Int get() = subtotalCents - rewardCents + taxCents

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

        bindPreferredCard()

        // Demo "scanner": tapping the cart rings up the next demo item.
        findViewById<ImageView>(R.id.cartIcon).setOnClickListener { scanNextItem() }

        findViewById<View>(R.id.signOutButton).setOnClickListener { signOut() }

        findViewById<Button>(R.id.rewardsRedeemButton).setOnClickListener {
            if (subtotalCents == 0) {
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

    private fun bindPreferredCard() {
        val card = CheckinSession.preferredCard
        findViewById<TextView>(R.id.cardLast4).text =
            card?.let { "#${it.last4}" } ?: getString(R.string.no_card_on_file)
    }

    private fun scanNextItem() {
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
        subtotalValue.text = money(subtotalCents)
        taxesValue.text = money(taxCents)
        totalValue.text = money(totalCents)
        rewardRow.visibility = if (rewardCents > 0) View.VISIBLE else View.GONE

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
        renderTotals()

        val amount = totalCents
        val orderId = currentOrderId
            ?: "BLM-${System.currentTimeMillis()}".also { currentOrderId = it }

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
                mainHandler.post {
                    if (isFinishing || isDestroyed) return@post
                    startActivity(
                        ThankYouActivity.intent(this, result.transactionId, result.amountCents),
                    )
                    finish()
                }
            } catch (e: Exception) {
                mainHandler.post {
                    if (isFinishing || isDestroyed) return@post
                    paying = false
                    payButton.text = getString(R.string.pay)
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
