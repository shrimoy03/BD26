# MerchantTerminal — mock POS (merchant-facing)

Mock department-store register for the WinkPay demo. Runs full-screen on the
Windows all-in-one at the counter and sends payment commands over the local
network to the Android customer-facing app (`../winkpos`) which embeds WinkPay.

Built with **Avalonia UI on .NET 10** — a native desktop app that develops and
runs on macOS and publishes to a self-contained Windows executable. The UI is a
replica of the real Bloomingdale's **AYS register** (from the "BLM pos flow"
deck, build 2026.6.1_1112): left basket panel, right prompt panel, the red
T1–T8 key grid, and the bottom status bar, on a fixed 1600×1000 canvas.

The register is a stage machine that walks the store flow screen for screen
(`ViewModels/MainViewModel.cs`, `RegisterStage`):

1. **Loyalty** — "Ask customer to Insert or Slide Bloomingdale's Card…";
   T1 Lookup Loyalty Number links `B.TEST · XXXXXXXXX8585`, F6 bypasses.
2. **Scan** — type/scan a UPC and press Enter (`3145891313406` Chanel Beaute
   50.00, `3365440057838` Ysl Cosmetics 30.00); Delete voids the selected
   line; T1 Checkout.
3. **Checkout** — T1 Bloomingdale's Card / Bloomingdale's Pay,
   T8 More Payment Methods; Esc returns to merchandise.
4. **More payments** — T2 bankcard (EMV), T3 Cash, etc.
5. **Cash** — key the amount, Enter tenders it.
   **Card/Pay** — hands the sale to the customer terminal (below); standalone
   it simulates card-in then the 89-second signature screen.
6. **Purchase Completed** — banner + "Updating Loyallist Program", then the
   register returns to the loyalty prompt on its own.

F3 cancels the transaction from any stage, F8 suspends (mock), F9 opens setup.

## Development (macOS)

```bash
# one-time: install the .NET SDK without sudo
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"

dotnet run          # opens the register in a 1280×800 window
```

On Windows the app starts in full-screen kiosk mode automatically
(`MainWindow.axaml.cs`).

## Publish for the Windows all-in-one

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# output: bin/Release/net10.0/win-x64/publish/ — copy the folder, run MerchantTerminal.exe
```

No .NET install is needed on the target machine. Fonts are bundled; the app
needs no internet access.

## Terminal link (protocol with the Android app)

The register hosts a WebSocket server the Android app connects to:

```
ws://<register-ip>:8181/pos
```

Every frame is one JSON object (camelCase). See `Models/PosMessages.cs`.

| Direction | type | fields |
|---|---|---|
| Android → POS | `HELLO` | — (optional greeting on connect) |
| POS → Android | `START_PAYMENT` | `orderId`, `amountCents`, `currency` |
| POS → Android | `CANCEL_PAYMENT` | `orderId` |
| Android → POS | `PAYMENT_RESULT` | `orderId`, `status` (`APPROVED` \| `DECLINED` \| `CANCELLED`), `amountCents?`, `method?` (shown as the tender label), `reason?` (shown on decline) |

Behavior on the register:
- **T1 Bloomingdale's Card / Bloomingdale's Pay** (checkout) sends
  `START_PAYMENT` with `method: BD_LOYALLIST` — the PxRetailer payment-options
  page where the customer can pick face/palm (WinkPay). **T2 Bankcard** sends
  `method: CARD` for straight EMV. With no terminal connected, the card flow is
  simulated (card-in → signature screen) so the demo still works standalone.
- The basket is mirrored to the terminal (`DISPLAY_CART`) on every change.
- Esc during the card stage sends `CANCEL_PAYMENT` and returns to checkout;
  F3 cancels the whole transaction.
- A `TENDER_SELECTED` event from the terminal (customer taps face/palm/card on
  the PxRetailer form) pulls the register into the card stage by itself.
- One client at a time; a new connection replaces the old one.

### Simulating the Android app

```bash
node --experimental-websocket tools/fake-terminal.mjs
```

Connects to the register and auto-approves any `START_PAYMENT` after 1.5 s.

## Setup screen (per-register configuration)

Press **F9** to open Terminal setup.
This is the supported way to configure a register — no environment variables and
no rebuild, so the same published build drops onto every store machine:

- **Connection** — wireless PXRRS, Wi-Fi WebSocket, or USB/PCL.
- **Terminal** — the PAX device's IP and port, with an HTTPS/mTLS toggle. The
  resolved REST base is previewed live.
- **This register** — the callback address PXRRS posts results to. Left blank it
  auto-detects the adapter that actually routes to the terminal, which matters on
  a store PC with several NICs (Windows' `169.254.*` link-local addresses are
  skipped). Override it only to pin a specific NIC.
- **Client certificate** — path to the PAX `.p12`, with a file picker and a
  found/missing indicator.
- **Test connection** — POSTs `getPackageList` and reports what came back,
  distinguishing a wrong IP (timeout), nothing listening (refused), and a
  missing/rejected client certificate (TLS handshake).

**Save & reconnect** writes the settings and rebuilds the live link in place —
no restart. Settings live in:

```
%APPDATA%\MerchantTerminal\settings.json
```

Copy that file to preconfigure another register. The `POS_*` environment
variables below still override the saved values when set; the setup screen shows
a warning listing any that are currently doing so.

## PAX-agreed link: JPXSS/PXRRS REST flow (jpxss mode)

The flow from PAX's BloomingdaleDemo sequence diagram: the register drives
PxRetailer form variables as mailboxes (`Services/JpxRestLink.cs`):
subscribe + `FOREGROUND=false` on startup; a sale is
`sendBatchCmd [SetVariable START_TRANS_REQ_DATA, DisplayForm StartTransaction]`;
the result comes back via a `notify IS_TRANS_STARTED=2` callback followed by
`getVariable TRANS_RESULT`. Works identically against JPxSerialServer on this
PC (USB-tethered terminal, PAX RS232-USB driver required) or against PXRRS on
the terminal's own IP over Ethernet/Wi-Fi (no JPXSS at all).

Configure it in the setup screen (F9) — or override from the environment:

```bash
POS_TERMINAL_LINK=jpxss dotnet run
# POS_JPXSS_URL   REST base            (default: built from the setup screen's
#                 host/port/TLS fields, else http://127.0.0.1:9090)
# POS_NOTIFY_URL  our notify callback  (default: auto-detected LAN IP on the
#                 configured notify port, e.g. http://192.168.1.149:8282/notify)
```

Terminal-side counterpart: `../winkpos` `link/PxrrsTransport.kt`
(`POS_LINK_MODE=pxrrs` in local.properties). Simulate the whole flow without
hardware (fake JPXSS/PXRRS + auto-approving WinkPay):

```bash
node tools/fake-jpxss.mjs
POS_TERMINAL_LINK=jpxss POS_TERMINAL_TEST_DIR=/tmp/jpxsstest dotnet run
```

Form/variable names come from the sequence diagram and are marked
`TODO(PAX)` pending the form package they ship.

### Running against a stock PxRetail package

The diagram assumes PAX's custom Bloomingdale's package. Verified against a live
A3700 (PxRetailer 2.01.16, PXRRS 1.16.42), a stock install defines **none** of
`START_TRANS_REQ_DATA`, `TRANS_RESULT`, `IS_TRANS_STARTED`, `StartTransaction`
or `EndTransaction`. What does exist is used instead, so the biometric flow runs
today:

| Diagram step | Stock equivalent |
|---|---|
| `FOREGROUND` | `BOOL.FOREGROUND` — PxDesigner variables are type-prefixed |
| `setVariable START_TRANS_REQ_DATA` + `DisplayForm StartTransaction` | basket mirrored to `LIST.ITEM` / `STR.SUBTOTAL` / `STR.TAX` / `STR.AMOUNTOK` |
| `notify IS_TRANS_STARTED=1` | `PAYMENTSTATUS` = `face`/`palm`/`card`, fired by the form's own tender buttons |
| `notify IS_TRANS_STARTED=2` then `getVariable TRANS_RESULT` | register polls `STR.TRANSACTION_RESULT`, which winkpos writes |

Two things to know about PXRRS itself, both learned the hard way:

- **The `replyURL` must be `https://`.** Notifications are not delivered to a
  plain-http callback, so the notify listener serves TLS using
  `certs/pxrrs-notify-server.p12` (the PAX *server* identity, distinct from the
  client cert). Disable with `NotifyUseTls: false` in settings.json.
- **`setVariable` rejects an empty value** with "invalid format", so a mailbox
  cannot be cleared. The result poll snapshots the variable and treats only a
  change as the current sale's result.

Biometric tenders are the one case that involves both links: WinkPay is a
separate Android app, so the register launches it over the WebSocket *and* tells
PxRetailer to drop the foreground (`BOOL.FOREGROUND=false`), in that order —
backgrounding first flashes the Android home screen.

## USB link via JPxSerialServer (PCL mode)

For a USB-tethered PAX terminal the register can talk through PAX's
JPxSerialServer instead of hosting the WebSocket. Same JSON protocol, framed
as PCL (`Services/PclFrameCodec.cs`): `STX | u16be len | TLVs | ETX | CRC32be`
over JPxSerialServer's raw TCP socket.

```bash
POS_TERMINAL_LINK=pcl dotnet run       # connect to 127.0.0.1:7001
# POS_PCL_HOST / POS_PCL_PORT override the JPxSerialServer address
```

Setup on the Windows box: install JPxSerialServer (needs Java 8), and in its
`application.properties` set `serverSocket.enabled=true` (port 7001). The
terminal-side counterpart lives in `../winkpos` (`link/PclSerialTransport.kt`),
pending PAX's NeptuneLite serial library — see `link/SerialIo.kt`. Open items
for PAX are marked `TODO(PAX)` in both codecs (ACK/NAK semantics, payload TLV
tag).

Simulate the whole USB path without hardware (fake JPxSerialServer+terminal,
auto-approves):

```bash
node tools/fake-pcl-terminal.mjs
POS_TERMINAL_LINK=pcl POS_TERMINAL_TEST_DIR=/tmp/pcltest dotnet run
```

## Headless screenshot / test modes

Environment variables (used for visual review without screen-recording
permissions — the app renders itself to PNG and exits):

- `POS_SCREENSHOT_DIR=<dir> dotnet run` — walks every screen in both themes,
  writes `01-register-dark.png` … `08-register-light.png`.
- `POS_TERMINAL_TEST_DIR=<dir> dotnet run` — waits for a terminal client,
  runs a card tender, captures awaiting/result states. Pair with
  `tools/fake-terminal.mjs`.

## Layout

```
App.axaml              design tokens (dark/light theme dictionaries), shared styles
Models/                Product, SaleLine, Payment, PromoDef, wire protocol
Services/TerminalLink.cs   Kestrel WebSocket host for the Android link
ViewModels/MainViewModel.cs   register state machine (basket, promos, tenders)
Views/                 RegisterView + Customer/Promos/Tender/Done overlays
Assets/Fonts/          Instrument Serif, IBM Plex Sans/Mono (bundled)
```

Branding note: "Meridian & Co." is the fictional retailer from the design
handoff. Swapping the wordmark/accent for real brand assets is confined to
`RegisterView.axaml` (wordmark block) and the `Accent` tokens in `App.axaml`.
