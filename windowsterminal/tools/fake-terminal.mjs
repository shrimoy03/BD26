// Simulates the Android winkpos app: connects, then approves any START_PAYMENT.
const ws = new WebSocket("ws://127.0.0.1:8181/pos");
ws.onopen = () => { console.log("terminal connected"); ws.send(JSON.stringify({ type: "HELLO" })); };
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  console.log("received:", e.data);
  if (m.type === "START_PAYMENT") {
    setTimeout(() => {
      ws.send(JSON.stringify({
        type: "PAYMENT_RESULT", orderId: m.orderId, amountCents: m.amountCents,
        status: "APPROVED", method: "Wink · Visa •••• 4417",
      }));
      console.log("approved", m.orderId, m.amountCents);
    }, 1500);
  }
};
ws.onerror = () => { console.log("connection error"); process.exit(1); };
setTimeout(() => process.exit(0), 25000);
