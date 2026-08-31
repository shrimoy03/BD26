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
