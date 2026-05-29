-- 1. Таблица пользователей CRM
CREATE TABLE IF NOT EXISTS crm_users (
    id SERIAL PRIMARY KEY,
    username VARCHAR(100) NOT NULL,
    email VARCHAR(255) NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Индекс на updated_at критически важен, чтобы Airflow быстро забирал только измененных пользователей (инкремент)
CREATE INDEX IF NOT EXISTS idx_crm_users_updated_at ON crm_users(updated_at);


-- 2. Таблица установленных протезов
CREATE TABLE IF NOT EXISTS crm_prosthetics (
    id SERIAL PRIMARY KEY,
    user_id INTEGER NOT NULL REFERENCES crm_users(id) ON DELETE CASCADE,
    device_id VARCHAR(50) UNIQUE NOT NULL, -- Уникальный серийный номер/ID датчика протеза
    model_name VARCHAR(100) NOT NULL,
    last_service_date DATE NOT NULL,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);


-- 3. Таблица сырых тиков телеметрии (куда поступают IoT-данные с датчиков)
CREATE TABLE IF NOT EXISTS telemetry_ticks (
    id BIGSERIAL PRIMARY KEY,
    device_id VARCHAR(50) NOT NULL REFERENCES crm_prosthetics(device_id) ON DELETE CASCADE,
    tick_timestamp TIMESTAMP NOT NULL,
    steps INTEGER DEFAULT 0,            -- Количество шагов, зафиксированных за этот тик
    active_seconds INTEGER DEFAULT 0,   -- Время активного использования в секундах
    battery_cycles INTEGER DEFAULT 0    -- Текущее количество циклов зарядки батареи
);

-- Индекс для оптимизации выборки Airflow по временным интервалам (target_date)
CREATE INDEX IF NOT EXISTS idx_telemetry_ticks_timestamp ON telemetry_ticks(tick_timestamp);

-- Добавим тестового пользователя (например, mryndin)
INSERT INTO crm_users (id, username, email, updated_at) 
VALUES (1, 'mryndin', 'mryndin@bionicpro.ru', CURRENT_TIMESTAMP)
ON CONFLICT DO NOTHING;

-- Привяжем к нему модель протеза
INSERT INTO crm_prosthetics (id, user_id, device_id, model_name, last_service_date)
VALUES (1, 1, 'DEV-BIO-999', 'Bionic-Hand-Ultra-v10', '2026-01-15')
ON CONFLICT DO NOTHING;

-- Набросаем сырых логов за май 2026 года (для симуляции работы датчиков)
INSERT INTO telemetry_ticks (device_id, tick_timestamp, steps, active_seconds, battery_cycles)
VALUES 
('DEV-BIO-999', '2026-05-27 08:00:00', 1200, 1800, 42),
('DEV-BIO-999', '2026-05-27 14:30:00', 2500, 3600, 42),
('DEV-BIO-999', '2026-05-27 21:00:00', 800, 900, 43)
ON CONFLICT DO NOTHING;