# Seguridad del canal de dispositivos (B3)

La plataforma recibe de los equipos por **dos canales distintos**:

| Canal | Puerto(s) | Equipos | Credencial | Cifrado |
|---|---|---|---|---|
| **MQTT** (broker embebido) | 1883 (claro) / **8883 (MQTTS, opt-in)** | ESP32 (Relay, GLP, Fligo, Toour, OTA) | **ApiKey** por dispositivo (en el username) | TLS disponible (opt-in) |
| **Gateway TCP binario** | 5023/5024/5027/5013/5001/5002 | Trackers GPS (GT06/SEEWORLD R12L-F, Mictrack, Queclink, Coban, iStartek, Jointech) | **IMEI** (en la trama de login) | No factible → aislamiento de red |

Por defecto ambos van **en claro**. Este documento explica cómo cifrar el canal MQTT (MQTTS) y
por qué el canal TCP se protege con **aislamiento de red**, no con TLS.

---

## 1. MQTTS (canal MQTT, dispositivos ESP32) — cifrado disponible

El broker MQTT está **embebido** en el proceso del API (MQTTnet). Soporta abrir, **además** del
1883 en claro, un endpoint cifrado **8883 (MQTTS)**. La autenticación por ApiKey y el ACL de
tópicos son **independientes del transporte**: se aplican idénticos sobre TLS (TLS solo añade
cifrado/integridad del canal y autentica al servidor).

### Habilitarlo

1. Coloca el certificado del broker en `./mqtt-certs/broker.pfx` (PFX con clave privada). Para
   staging puedes autofirmarlo:
   ```bash
   mkdir -p mqtt-certs
   openssl req -x509 -nodes -newkey rsa:2048 -days 365 \
     -keyout mqtt-certs/broker.key -out mqtt-certs/broker.crt \
     -subj "/CN=mqtt.tu-dominio.com"
   openssl pkcs12 -export -out mqtt-certs/broker.pfx \
     -inkey mqtt-certs/broker.key -in mqtt-certs/broker.crt -passout pass:TU_PASSWORD
   ```
   (Alternativa PEM: deja `MQTT_TLS_CERT_PATH` apuntando al `.crt` y pon `MQTT_TLS_CERT_KEY_PATH` al `.key`.)

2. En tu `.env`:
   ```ini
   MQTT_TLS=true
   MQTT_TLS_CERT_PATH=/app/mqtt-certs/broker.pfx
   MQTT_TLS_CERT_PASSWORD=TU_PASSWORD        # secreto; nunca en appsettings ni en logs
   # Para MQTTS EXCLUSIVO (cerrar el 1883 en claro):
   # MQTT_DISABLE_PLAINTEXT=true
   ```

3. Recrea el API: `docker compose up -d --build iot-api`. El broker abrirá el 8883 cifrado
   (y el 1883 salvo que lo deshabilites). Si `MQTT_TLS=true` pero falta el certificado, el API
   **falla al arrancar** con un error claro (no se queda anunciando TLS sin listener).

4. En el firmware ESP32: conectar con `WiFiClientSecure` al host del broker, puerto **8883**,
   confiando en el certificado (si es autofirmado, embeber el `broker.crt` como *trust anchor*;
   si es de una CA pública/privada, usar su raíz). La ApiKey viaja igual, pero ya cifrada.

> **No fuerces MQTTS sin inventariar los firmwares**: solo habilita `MQTT_DISABLE_PLAINTEXT=true`
> cuando TODOS los equipos MQTT en producción soporten TLS, o dejarías fuera a los que no.

### Ciclo de vida del certificado

Certificado autofirmado (hay que provisionar el *trust anchor* en el firmware) **o** de una CA.
Rota antes de que caduque; un cert caducado/no confiado tira todas las conexiones TLS. La
contraseña del PFX es un secreto: gestiónala con el mecanismo de secretos (env/vault), nunca en
`appsettings` en claro ni en logs.

---

## 2. Gateways TCP binarios (trackers GPS) — aislamiento de red

**TLS sobre el socket crudo NO es factible para la flota de trackers.** Estos equipos (GT06/Concox,
Coban, Mictrack, Queclink, iStartek, Jointech, SEEWORLD R12L-F) abren un socket TCP plano y lo
primero que envían es su trama binaria de aplicación (p.ej. GT06 `0x78 0x78 ...`), no un
`ClientHello` TLS. La mayoría son 2G/4G baratos **sin pila TLS**. Si el servidor exigiera handshake
TLS, esperaría un `ClientHello` que nunca llega y el equipo **jamás conectaría**. Además, la regla
del proyecto es que **la plataforma se adapta, no los firmwares**.

Riesgo real a cubrir: en el canal TCP la **única** credencial es el **IMEI**, que viaja en claro y
está impreso en la etiqueta del equipo. Quien pueda alcanzar el puerto y conozca un IMEI válido y
activo puede **suplantar** al dispositivo (posiciones/alarmas falsas). La compuerta de adopción
(`AdminStatus==Active`) comprueba que el IMEI exista y esté activo, pero no impide la suplantación.

### Control correcto: aislamiento de red (entregable que cierra B3)

1. **No exponer los puertos de dispositivos a Internet.** Hoy `docker-compose.yml` publica
   `1883` y `5001/5002/5013/5023/5024/5027` en `0.0.0.0` (`ports:`), alcanzables desde Internet si
   el host tiene IP pública. En producción, **vincula a una interfaz privada** (p.ej.
   `10.x.y.z:5023:5023`) detrás del túnel, o usa `expose:` + red interna + un ingreso endurecido.
2. **APN privada / VPN del operador móvil** para las SIMs de los trackers: que su único
   encaminamiento sea un túnel VPN/IPsec hacia la VPC de ingesta y nunca toquen Internet público.
   Es el control estándar de flotas celulares y resuelve B3 para el grueso de trackers.
3. **Firewall/security-group default-deny con allowlist**: solo los rangos de egreso conocidos del
   operador (o el peer VPN) alcanzan los puertos del gateway. **Documenta** esos rangos.
4. **Proxy TLS-terminador** (stunnel/HAProxy/Envoy) **solo** para el subconjunto de equipos con
   firmware TLS (4G/Cat-M/NB-IoT capaces): termina TLS en el proxy y reenvía en claro al listener
   por loopback/red privada. No requiere tocar el gateway; añade un puerto TLS alterno.

> El canal MQTT 1883 en claro comparte el mismo problema de confidencialidad (la ApiKey viaja en
> el username): aíslalo igual por red mientras no uses MQTTS exclusivo.

---

## Checklist de endurecimiento del canal de dispositivos

- [ ] MQTTS (8883) habilitado para los equipos MQTT que soporten TLS; `MQTT_DISABLE_PLAINTEXT=true`
      cuando todos lo soporten.
- [ ] Certificado del broker provisionado y con proceso de rotación; password del PFX como secreto.
- [ ] Puertos de dispositivos (1883/5001-5027) **no** publicados en `0.0.0.0`: bind privado o `expose:`.
- [ ] APN privada/VPN para las SIMs de los trackers; firewall default-deny con allowlist documentada.
- [ ] (Opcional) proxy TLS-terminador para el subconjunto de trackers TLS-capaces.
- [ ] Documentado qué equipos van cifrados, cuáles por aislamiento y qué rangos/peers se permiten.

> Nota aparte (no es B3): `MailKitEmailService` acepta cualquier certificado del servidor SMTP
> (`ServerCertificateValidationCallback => true`), lo que habilita MITM del correo saliente.
> Conviene validar la cadena del SMTP; es un endurecimiento independiente del canal de dispositivos.
