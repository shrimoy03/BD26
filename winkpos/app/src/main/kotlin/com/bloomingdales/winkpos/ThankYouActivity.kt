package com.bloomingdales.winkpos

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import java.util.Locale

/** Post-payment "Thanks for Shopping with us" screen; returns to Welcome. */
class ThankYouActivity : AppCompatActivity() {

    private val handler = Handler(Looper.getMainLooper())
    private val goHome = Runnable {
        CheckinSession.clear()
        val intent = Intent(this, WelcomeActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        startActivity(intent)
        finish()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_thank_you)
        enterKioskMode()

        val txnId = intent.getStringExtra(EXTRA_TXN_ID).orEmpty()
        val amountCents = intent.getIntExtra(EXTRA_AMOUNT_CENTS, 0)
        findViewById<TextView>(R.id.receiptText).text = if (txnId.isNotEmpty()) {
            getString(
                R.string.receipt_line,
                String.format(Locale.US, "$%,.2f", amountCents / 100.0),
                txnId,
            )
        } else {
            ""
        }

        handler.postDelayed(goHome, 6_000)
        findViewById<android.view.View>(android.R.id.content).setOnClickListener {
            handler.removeCallbacks(goHome)
            goHome.run()
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        handler.removeCallbacks(goHome)
    }

    companion object {
        private const val EXTRA_TXN_ID = "txnId"
        private const val EXTRA_AMOUNT_CENTS = "amountCents"

        fun intent(context: Context, transactionId: String, amountCents: Int): Intent =
            Intent(context, ThankYouActivity::class.java)
                .putExtra(EXTRA_TXN_ID, transactionId)
                .putExtra(EXTRA_AMOUNT_CENTS, amountCents)
    }
}
