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
