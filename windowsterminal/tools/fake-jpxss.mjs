// Simulates the PAX-agreed REST flow (BloomingdaleDemo sequence diagram):
// a JPxSerialServer/PXRRS lookalike on :9090 plus an auto-approving WinkPay
// app behind it. Run the register with POS_TERMINAL_LINK=jpxss.
//
//   /getPackageList /setVariable /subscribe /sendBatchCmd /getVariable
//   DisplayForm StartTransaction -> notify IS_TRANS_STARTED=1, then the fake
//   "WinkPay" approves after 1.5 s: sets TRANS_RESULT, notify IS_TRANS_STARTED=2.
import http from "node:http";

const variables = new Map();
const subscribers = new Set();
const listItems = []; // LIST.ITEM rows as the register renders the basket

const ok = (extra = {}) => JSON.stringify({ message: "OK", resultCode: "0", ...extra });

function notifyRaw(payload, label) {
  for (const url of subscribers) {
    const req = http.request(url, { method: "POST", headers: { "Content-Type": "application/json" } });
    req.on("error", (e) => console.log("notify failed:", url, e.message));
    req.end(JSON.stringify(payload));
  }
  console.log(`notify -> ${label} (${subscribers.size} subscriber(s))`);
}

function notifyAll(name, value) {
  notifyRaw({ name, value }, `${name}=${value}`);
}

// Contactless reader: the register arms it (emvBeginContactlessTxn) and the
// "customer" taps a Visa 1.5 s later. FAKE_TAP=timeout makes the first arm
// time out (0x86) so the re-arm path is exercised; FAKE_TAP=cancel reports a
// customer cancel (0x88).
let tapBehaviour = process.env.FAKE_TAP ?? "tap";
function fakeTap(tlvs) {
  const amount = tlvs.find((t) => t.tag === "9F02")?.value;
  console.log(`contactless armed for amount ${amount}; behaviour=${tapBehaviour}`);
  setTimeout(() => {
    if (tapBehaviour === "timeout") {
      tapBehaviour = "tap"; // only the first arm times out
      notifyRaw({ commandName: "EMVBeginContactlessTxn", resultCode: "0x86", message: "time out" }, "EMV clss timeout");
    } else if (tapBehaviour === "cancel") {
      notifyRaw({ commandName: "EMVBeginContactlessTxn", resultCode: "0x88", message: "user cancel" }, "EMV clss user cancel");
    } else {
      notifyRaw({
        commandName: "EMVBeginContactlessTxn", resultCode: "0", message: "success",
        tlvs: [
          { kernelType: 0, tag: "50", value: "56495341" },            // "VISA"
          { kernelType: 0, tag: "5F20", value: "4A20444F45" },        // "J DOE"
          { kernelType: 0, tag: "C7", value: "7B226D61736B6564504E223A2234303030202A2A2A2A202A2A2A2A2031323334227D" },
          { kernelType: 0, tag: "9F02", value: amount },
          { kernelType: 0, tag: "9F27", value: "80" },
        ],
      }, "EMV clss tap ok");
    }
  }, 1500);
}

function fakeWinkPayApproves() {
  const reqJson = variables.get("START_TRANS_REQ_DATA");
  const m = JSON.parse(reqJson ?? "{}");
  console.log("fake WinkPay read START_TRANS_REQ_DATA:", reqJson);
  if (m.type !== "START_PAYMENT") return; // e.g. CANCEL_PAYMENT: just go idle
  setTimeout(() => {
    variables.set("TRANS_RESULT", JSON.stringify({
      type: "PAYMENT_RESULT", orderId: m.orderId, amountCents: m.amountCents,
      status: "APPROVED", method: "Wink · Visa •••• 4417", token: "wct_demo_9f2a71",
    }));
    notifyAll("IS_TRANS_STARTED", "2");
  }, 1500);
}

function handle(pathname, params, body, res) {
  switch (pathname) {
    case "/getPackageList":
      return res.end(ok({ resultItems: ["PxRetail"] }));
    case "/subscribe": {
      const reply = params.get("replyURL");
      if (reply) subscribers.add(reply);
      console.log("subscribed:", reply);
      return res.end(ok());
    }
    case "/setVariable": {
      for (const v of JSON.parse(body).variables ?? []) variables.set(v.name, v.value);
      return res.end(ok());
    }
    case "/getVariable": {
      const names = (params.get("variableNames") ?? "").split(",");
      const resultItems = names.map((name) => ({ name, value: variables.get(name) ?? "" }));
      console.log("getVariable:", names.join(","));
      return res.end(ok({ resultItems }));
    }
    case "/emvBeginContactlessTxn": {
      fakeTap(JSON.parse(body || "[]"));
      return res.end(JSON.stringify({ message: "In progress", resultCode: "0" }));
    }
    case "/emvReleaseContactlessService": {
      console.log("emvReleaseContactlessService");
      return res.end(ok({ message: "EMVReleaseContactlessService Released" }));
    }
    case "/emvEndContactlessTxn": {
      console.log("emvEndContactlessTxn");
      setTimeout(() => notifyRaw({ commandName: "EMVEndContactlessTxn", resultCode: "0", message: "success" }, "EMV clss end ok"), 300);
      return res.end(JSON.stringify({ message: "In progress", resultCode: "0" }));
    }
    case "/listBoxRemoveItem": {
      // No itemId -> clear the whole list (how the register rebuilds the basket).
      listItems.length = 0;
      console.log("listBoxRemoveItem: cleared");
      return res.end(ok());
    }
    case "/sendBatchCmd": {
      const cmds = JSON.parse(body);
      console.log("sendBatchCmd:", cmds.map((c) => c.commandName).join(" + "));
      for (const c of cmds) {
        if (c.commandName === "SetVariable") {
          for (const v of c.variables ?? []) variables.set(v.name, v.value);
        } else if (c.commandName === "ListBoxInsertItem") {
          for (const item of c.listItems ?? []) listItems.push(item.text);
          console.log("  LIST.ITEM now:", JSON.stringify(listItems));
        } else if (c.commandName === "DisplayForm" && c.formName === "StartTransaction") {
          notifyAll("IS_TRANS_STARTED", "1");
          fakeWinkPayApproves();
        } else if (c.commandName === "DisplayForm" && c.formName === "EndTransaction") {
          notifyAll("IS_TRANS_STARTED", "2");
        }
      }
      return res.end(ok());
    }
    default:
      res.statusCode = 404;
      return res.end(JSON.stringify({ resultCode: "1", message: `unknown: ${pathname}` }));
  }
}

http.createServer((req, res) => {
  const url = new URL(req.url, "http://localhost");
  let body = "";
  req.on("data", (c) => (body += c));
  req.on("end", () => {
    res.setHeader("Content-Type", "application/json");
    try {
      handle(url.pathname, url.searchParams, body, res);
    } catch (e) {
      res.statusCode = 500;
      res.end(JSON.stringify({ resultCode: "1", message: e.message }));
    }
  });
}).listen(9090, "127.0.0.1", () => console.log("fake JPXSS/PXRRS listening on 127.0.0.1:9090"));

setTimeout(() => process.exit(0), 60000);
