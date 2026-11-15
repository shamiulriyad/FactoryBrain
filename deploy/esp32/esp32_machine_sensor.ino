// BunonBrain ESP32 machine sensor + piece counter (example firmware).
//
// What it does:
//   * measures machine current with a CT clamp (analog) and reports power
//     telemetry every 5 s on factory/<FACTORY>/device/<SENSOR>/telemetry
//     (the backend turns power into RUNNING / IDLE / STOPPED with the
//     machine's thresholds);
//   * counts pieces from a sensor on a GPIO (debounced interrupt) and sends
//     increments on factory/<FACTORY>/machine/<MACHINE>/production;
//   * sends a heartbeat every 30 s, and sets an MQTT last will so the broker
//     publishes OFFLINE for it if the ESP32 loses power or Wi-Fi;
//   * stamps every message with a unique messageId and a UTC time (NTP), so
//     a resend after a reconnect is recorded once.
//
// Libraries: PubSubClient (Nick O'Leary), ArduinoJson 7.
// Register two devices in BunonBrain first (Cameras & devices): the sensor
// (category MachineSensor, bound to the machine) and the counter (category
// ProductionCounter, bound to the same machine); set both to protocol MQTT.
// Credentials below are placeholders: give every device its own broker
// account, never reuse the backend's.
//
// One ESP32, two registered devices: the heartbeat below keeps the sensor's
// health; the counter's health is refreshed by its counts. Give the counter a
// longer offline window (OfflineAfterSeconds) than your longest pause between
// pieces, or let the ESP32 also use the counter's own broker account and
// publish a heartbeat for it, so a quiet counter isn't reported disconnected.

#include <WiFi.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include <time.h>

const char* WIFI_SSID     = "factory-iot";
const char* WIFI_PASSWORD = "<wifi-password>";
const char* MQTT_HOST     = "192.168.10.20";
const int   MQTT_PORT     = 1883;          // 8883 with WiFiClientSecure + CA certificate
const char* MQTT_USER     = "S-L1-T025";   // broker account = sensor device code
const char* MQTT_PASSWORD = "<device-broker-password>";

const char* FACTORY = "MAIN";
const char* SENSOR  = "S-L1-T025";         // device code of the sensor
const char* COUNTER = "PC-L1-T025";        // device code of the piece counter
const char* MACHINE = "M-0101";            // machine code
const char* FIRMWARE = "1.0.0";

const int CT_PIN = 34;                     // analog input of the CT clamp circuit
const int PIECE_PIN = 27;                  // piece sensor (active low)
const float MAINS_VOLTAGE = 230.0;
const float AMPS_PER_COUNT = 0.0049;       // calibrate for your clamp and burden resistor

WiFiClient net;
PubSubClient mqtt(net);
volatile uint32_t pendingPieces = 0;
volatile uint32_t lastPieceMs = 0;
uint32_t sequence = 0;
uint32_t lastTelemetry = 0, lastHeartbeat = 0;

String topic(const char* device, const char* channel) { return String("factory/") + FACTORY + "/device/" + device + "/" + channel; }
String productionTopic() { return String("factory/") + FACTORY + "/machine/" + MACHINE + "/production"; }

String utcNow() {
  time_t now = time(nullptr);
  struct tm t;
  gmtime_r(&now, &t);
  char buf[32];
  strftime(buf, sizeof(buf), "%Y-%m-%dT%H:%M:%SZ", &t);
  return String(buf);
}

// Unique per device across reboots: chip id + boot time + sequence.
String messageId(const char* kind) {
  static uint64_t chip = ESP.getEfuseMac();
  static time_t boot = time(nullptr);
  return String(kind) + "-" + String((uint32_t)(chip & 0xFFFFFF), HEX) + "-" + String((uint32_t)boot) + "-" + String(++sequence);
}

void IRAM_ATTR onPiece() {
  uint32_t now = millis();
  if (now - lastPieceMs > 300) { pendingPieces++; lastPieceMs = now; }   // 300 ms debounce
}

float readAmps() {
  // RMS over ~200 ms around the ADC midpoint.
  const int samples = 2000;
  double sum = 0;
  for (int i = 0; i < samples; i++) { int v = analogRead(CT_PIN) - 2048; sum += (double)v * v; delayMicroseconds(100); }
  return sqrt(sum / samples) * AMPS_PER_COUNT;
}

bool publish(const String& t, JsonDocument& doc) {
  doc["schema"] = "bunonbrain.iot.v1";
  doc["occurredAt"] = utcNow();
  char payload[512];
  size_t n = serializeJson(doc, payload, sizeof(payload));
  return mqtt.publish(t.c_str(), (const uint8_t*)payload, n, false);
}

void connect() {
  while (WiFi.status() != WL_CONNECTED) { delay(500); }
  while (!mqtt.connected()) {
    // Last will: the broker announces OFFLINE if this device disappears.
    String will = String("{\"messageId\":\"lwt-") + SENSOR + "-" + String((uint32_t)time(nullptr)) + "\",\"status\":\"OFFLINE\",\"message\":\"Connection lost (last will)\"}";
    if (mqtt.connect(SENSOR, MQTT_USER, MQTT_PASSWORD, topic(SENSOR, "status").c_str(), 1, false, will.c_str())) {
      JsonDocument online;
      online["messageId"] = messageId("on");
      online["status"] = "ONLINE";
      online["firmwareVersion"] = FIRMWARE;
      publish(topic(SENSOR, "status"), online);
    } else delay(2000);
  }
}

void setup() {
  pinMode(PIECE_PIN, INPUT_PULLUP);
  attachInterrupt(digitalPinToInterrupt(PIECE_PIN), onPiece, FALLING);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  configTime(0, 0, "pool.ntp.org");        // UTC: the backend refuses messages more than 2 min in the future
  mqtt.setServer(MQTT_HOST, MQTT_PORT);
  mqtt.setBufferSize(1024);
  connect();
}

void loop() {
  if (!mqtt.connected()) connect();
  mqtt.loop();
  uint32_t now = millis();

  if (now - lastTelemetry >= 5000) {
    lastTelemetry = now;
    float amps = readAmps();
    JsonDocument t;
    t["messageId"] = messageId("t");
    t["currentAmps"] = amps;
    t["powerWatts"] = amps * MAINS_VOLTAGE;  // no "state": the backend derives it from the machine's power thresholds
    publish(topic(SENSOR, "telemetry"), t);
  }

  uint32_t pieces;
  noInterrupts(); pieces = pendingPieces; pendingPieces = 0; interrupts();
  if (pieces > 0) {
    JsonDocument p;
    p["messageId"] = messageId("p");
    p["deviceId"] = COUNTER;
    JsonObject counter = p["counter"].to<JsonObject>();
    counter["mode"] = "INCREMENT";
    counter["value"] = pieces;
    if (!publish(productionTopic(), p)) { noInterrupts(); pendingPieces += pieces; interrupts(); }   // retry next loop, same pieces
  }

  if (now - lastHeartbeat >= 30000) {
    lastHeartbeat = now;
    JsonDocument h;
    h["type"] = "heartbeat";
    h["messageId"] = messageId("h");
    h["firmwareVersion"] = FIRMWARE;
    JsonObject metrics = h["metrics"].to<JsonObject>();
    metrics["rssi"] = WiFi.RSSI();
    metrics["uptimeSeconds"] = now / 1000;
    metrics["freeHeap"] = ESP.getFreeHeap();
    publish(topic(SENSOR, "heartbeat"), h);
  }
}
