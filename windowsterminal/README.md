# MerchantTerminal — mock POS (merchant-facing)

Mock department-store register for the WinkPay demo. Runs full-screen on the
Windows all-in-one at the counter and sends payment commands over the local
network to the Android customer-facing app (`../winkpos`) which embeds WinkPay.

Built with **Avalonia UI on .NET 10** — a native desktop app that develops and
runs on macOS and publishes to a self-contained Windows executable. The UI
implements the register design handoff, styled with the Bloomingdale's Figma assets
(wordmark, Loyallist art, catalog items) (1600×1000 register canvas,
dark + light themes, basket / quick keys / customer lookup / promotions /
split tender / sale complete).

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
- The **Credit / debit card** tender sends `START_PAYMENT` for the outstanding
  balance when a terminal is connected (tile hint switches to
  "Customer terminal · chip, tap, or Wink"); with no terminal connected it
  falls back to an instant mock approval so the demo still works standalone.
- While awaiting, the tender tiles disable and a Cancel banner appears;
  Cancel sends `CANCEL_PAYMENT`.
- One client at a time; a new connection replaces the old one.

### Simulating the Android app

```bash
node --experimental-websocket tools/fake-terminal.mjs
```

Connects to the register and auto-approves any `START_PAYMENT` after 1.5 s.

## PAX-agreed link: JPXSS/PXRRS REST flow (jpxss mode)

The flow from PAX's BloomingdaleDemo sequence diagram: the register drives
PxRetailer form variables as mailboxes (`Services/JpxRestLink.cs`):
subscribe + `FOREGROUND=false` on startup; a sale is
`sendBatchCmd [SetVariable START_TRANS_REQ_DATA, DisplayForm StartTransaction]`;
the result comes back via a `notify IS_TRANS_STARTED=2` callback followed by
`getVariable TRANS_RESULT`. Works identically against JPxSerialServer on this
PC (USB-tethered terminal, PAX RS232-USB driver required) or against PXRRS on
the terminal's own IP over Ethernet/Wi-Fi (no JPXSS at all).

```bash
POS_TERMINAL_LINK=jpxss dotnet run
# POS_JPXSS_URL   REST base            (default http://127.0.0.1:9090)
# POS_NOTIFY_URL  our notify callback  (default http://127.0.0.1:8282/notify —
#                 use this PC's LAN IP when talking to the terminal directly)
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
