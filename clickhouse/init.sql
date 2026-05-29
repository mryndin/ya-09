CREATE TABLE IF NOT EXISTS default.v_user_prosthetic_reports (
    user_id UInt64,
    report_date Date,
    model_name String,
    steps_count UInt32,
    active_hours Float32,
    battery_cycles UInt16,
    last_service_date Date,
    sync_updated_at DateTime
) ENGINE = ReplacingMergeTree(sync_updated_at)
PRIMARY KEY (user_id)
ORDER BY (user_id, report_date);