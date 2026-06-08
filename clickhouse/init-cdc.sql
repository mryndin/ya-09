-- 1. Буферная таблица для чтения сырого текста из Kafka
CREATE TABLE IF NOT EXISTS default.kafka_prosthetic_reports_queue
(
    raw_json String
)
ENGINE = Kafka
SETTINGS 
    kafka_broker_list = 'kafka:9092',
    kafka_topic_list = 'crm_prod.public.crm_prosthetics',
    kafka_group_name = 'ch_cdc_group_prod',
    kafka_format = 'LineAsString',
    kafka_num_consumers = 1;

-- 2. Целевая таблица-витрина
CREATE TABLE IF NOT EXISTS default.v_user_prosthetic_reports_cdc
(
    id Int32,
    user_guid UUID,
    device_id String,
    model_name String,
    last_service_date Date,
    updated_at DateTime64(6),
    sign Int8,
    version UInt64
)
ENGINE = ReplacingMergeTree(version)
PRIMARY KEY (user_guid, id)
ORDER BY (user_guid, id);

-- 3. Материализованное представление с разбором JSON
CREATE MATERIALIZED VIEW IF NOT EXISTS default.mv_kafka_to_prosthetic_reports TO default.v_user_prosthetic_reports_cdc AS
SELECT
    JSONExtractInt(raw_json, 'after', 'id') AS id,
    toUUID(JSONExtractString(raw_json, 'after', 'user_guid')) AS user_guid,
    JSONExtractString(raw_json, 'after', 'device_id') AS device_id,
    JSONExtractString(raw_json, 'after', 'model_name') AS model_name,
    toDate(JSONExtractInt(raw_json, 'after', 'last_service_date')) AS last_service_date,
    fromUnixTimestamp64Micro(JSONExtractInt(raw_json, 'after', 'updated_at')) AS updated_at,
    if(JSONExtractString(raw_json, 'op') = 'd', -1, 1) AS sign,
    toUInt64(JSONExtractInt(raw_json, 'after', 'updated_at')) AS version
FROM default.kafka_prosthetic_reports_queue
WHERE JSONExtractString(raw_json, 'op') != 'd' 
  AND JSONExtractString(raw_json, 'after', 'user_guid') != '';