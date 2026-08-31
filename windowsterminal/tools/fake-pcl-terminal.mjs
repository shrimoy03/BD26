// Simulates JPxSerialServer's raw PCL TCP socket plus the winkpos app behind
// it: listens on 7001, decodes PCL frames, approves any START_PAYMENT.
// Run the register with POS_TERMINAL_LINK=pcl to exercise the USB code path
// end-to-end without hardware.
//
// Framing (see Services/PclFrameCodec.cs / winkpos link/PclFrameCodec.kt):
//   STX(0x02) | u16be payloadLen | TLVs | ETX(0x03) | CRC32be(STX..ETX)
//   TLV: u16be tag | u16be len | value. 0xE100 carries the WinkPos JSON.
import net from "node:net";
import zlib from "node:zlib";

const STX = 0x02, ETX = 0x03, TAG_JSON = 0xe100;

function encodeFrame(tlvs) {
  const payload = Buffer.concat(tlvs.map(([tag, value]) => {
    const head = Buffer.alloc(4);
    head.writeUInt16BE(tag, 0);
    head.writeUInt16BE(value.length, 2);
    return Buffer.concat([head, value]);
  }));
  const body = Buffer.alloc(3 + payload.length + 1);
  body[0] = STX;
  body.writeUInt16BE(payload.length, 1);
  payload.copy(body, 3);
  body[body.length - 1] = ETX;
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(zlib.crc32(body) >>> 0, 0);
  return Buffer.concat([body, crc]);
}

function encodeMessage(obj) {
  return encodeFrame([
    [0xe000, Buffer.from("WINKPOS", "ascii")],
    [0xe001, Buffer.from("POS", "ascii")],
    [TAG_JSON, Buffer.from(JSON.stringify(obj), "utf8")],
  ]);
}

function makeDecoder(onJson) {
  let buf = Buffer.alloc(0);
  return (chunk) => {
    buf = Buffer.concat([buf, chunk]);
    for (;;) {
      const start = buf.indexOf(STX);
      if (start < 0) { buf = Buffer.alloc(0); return; }
      buf = buf.subarray(start);
      if (buf.length < 3) return;
      const len = buf.readUInt16BE(1);
      const total = 3 + len + 1 + 4;
      if (buf.length < total) return;
      const frame = buf.subarray(0, total);
      const crcOk = (zlib.crc32(frame.subarray(0, total - 4)) >>> 0) === frame.readUInt32BE(total - 4);
      if (frame[3 + len] !== ETX || !crcOk) { buf = buf.subarray(1); continue; }
      buf = buf.subarray(total);
      for (let i = 3; i + 4 <= 3 + len;) {
        const tag = frame.readUInt16BE(i), tlvLen = frame.readUInt16BE(i + 2);
        if (tag === TAG_JSON) onJson(frame.subarray(i + 4, i + 4 + tlvLen).toString("utf8"));
        i += 4 + tlvLen;
      }
    }
  };
}

const server = net.createServer((socket) => {
  console.log("register connected");
  const decode = makeDecoder((json) => {
    console.log("received:", json);
    const m = JSON.parse(json);
    if (m.type === "START_PAYMENT") {
      setTimeout(() => {
        socket.write(encodeMessage({
          type: "PAYMENT_RESULT", orderId: m.orderId, amountCents: m.amountCents,
          status: "APPROVED", method: "Wink · Visa •••• 4417",
        }));
        console.log("approved", m.orderId, m.amountCents);
      }, 1500);
    }
  });
  socket.on("data", decode);
  socket.on("close", () => console.log("register disconnected"));
  socket.write(encodeMessage({ type: "HELLO" }));
});

server.listen(7001, "127.0.0.1", () => console.log("fake PCL socket listening on 127.0.0.1:7001"));
setTimeout(() => process.exit(0), 60000);
