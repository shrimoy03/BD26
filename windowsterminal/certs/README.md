# PXRRS mutual-TLS client certificate

`pxrrs-integration-client.p12` is the PAX *integration* client identity that
PxRetailerRestService (on the terminal) requires for HTTPS. Password: `pax12345`.

Regenerate from the JPxSerialServer bundle if missing:

    keytool -importkeystore \
      -srckeystore "<JPxSerialServer>/Certificate/integrationCustomer.jks" \
      -srcstorepass pax12345 -srcalias integrationcustomer \
      -destkeystore pxrrs-integration-client.p12 \
      -deststoretype PKCS12 -deststorepass pax12345

Integration-cert access may be feature-restricted by PAX; production merchants
replace it with a merchant CA cert via the setMerchantCACertificate API.

# PXRRS notify-callback server identity

`pxrrs-notify-server.p12` is the server identity the register's notify
listener presents (PAX Multilane `*.pax.com`), and the certificate that gets
attached to `/subscribe` (curl equivalent: `--form 'fileName=@server_pci7.cert'`).
Password: `pax12345`. Both .p12 files are gitignored — regenerate from the PAX
PCI7 bundle (`key/Certificate_PCI7/`):

    openssl pkcs12 -export \
      -in server_pci7.cert -inkey server.key \
      -out pxrrs-notify-server.p12 -name pxrrs-notify \
      -passout pass:pax12345

## Android copies must use the legacy PKCS#12 encoding

The `.p12` files copied into `winkpos/app/src/main/assets/` must be exported
with `-keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1`. OpenSSL 3's
default (PBES2 / AES-256 / HMAC-SHA256) is unreadable on Android 11 (the
A3700: "exception unwrapping private key - NoSuchAlgorithmException"), so the
app presented no client certificate and every PXRRS call failed with
TLSV1_CERTIFICATE_REQUIRED, while the same file worked on the Android 14 A380.
Re-export when the certificates change:

```bash
openssl pkcs12 -in certs/pxrrs-integration-client.p12 -passin pass:pax12345 -nodes -out /tmp/c.pem
openssl pkcs12 -export -in /tmp/c.pem -out winkpos/app/src/main/assets/pxrrs-integration-client.p12 \
  -passout pass:pax12345 -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1
rm /tmp/c.pem   # same for pxrrs-notify-server.p12
```
