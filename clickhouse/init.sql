-- Удаляем старую витрину, если она была
DROP TABLE IF EXISTS default.v_user_prosthetic_reports;

-- Создаем витрину с типом UUID
CREATE TABLE default.v_user_prosthetic_reports (
    user_guid UUID,
    report_date Date,
    model_name String,
    steps_count UInt32,
    active_hours Float32,
    battery_cycles UInt16,
    last_service_date Date,
    sync_updated_at DateTime
) ENGINE = ReplacingMergeTree(sync_updated_at)
PRIMARY KEY (user_guid)
ORDER BY (user_guid, report_date);