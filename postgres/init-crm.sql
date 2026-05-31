-- Очистка старой структуры перед инициализацией
DROP TABLE IF EXISTS telemetry_ticks CASCADE;
DROP TABLE IF EXISTS crm_prosthetics CASCADE;
DROP TABLE IF EXISTS crm_users CASCADE;

-- 1. Таблица пользователей CRM (с UUID из LDAP в качестве PK)
CREATE TABLE crm_users (
    user_guid UUID PRIMARY KEY,
    username VARCHAR(100) NOT NULL,
    email VARCHAR(255) NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_crm_users_updated_at ON crm_users(updated_at);

-- 2. Таблица установленных протезов (связь по UUID)
CREATE TABLE crm_prosthetics (
    id SERIAL PRIMARY KEY,
    user_guid UUID NOT NULL REFERENCES crm_users(user_guid) ON DELETE CASCADE,
    device_id VARCHAR(50) UNIQUE NOT NULL, 
    model_name VARCHAR(100) NOT NULL,
    last_service_date DATE NOT NULL,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- 3. Таблица сырых тиков телеметрии (привязка к железке device_id)
CREATE TABLE telemetry_ticks (
    id BIGSERIAL PRIMARY KEY,
    device_id VARCHAR(50) NOT NULL REFERENCES crm_prosthetics(device_id) ON DELETE CASCADE,
    tick_timestamp TIMESTAMP NOT NULL,
    steps INTEGER DEFAULT 0,
    active_seconds INTEGER DEFAULT 0,
    battery_cycles INTEGER DEFAULT 0
);

CREATE INDEX idx_telemetry_ticks_timestamp ON telemetry_ticks(tick_timestamp);

-- ========================================================
-- НАПОЛНЕНИЕ ТЕСТОВЫМИ ДАННЫМИ
-- ========================================================

-- Вместо жесткого UUID пишем маркер:
INSERT INTO crm_users (user_guid, username, email, updated_at) 
VALUES ('{{JOHN_DOE_UUID}}', 'john.doe', 'john@example.com', CURRENT_TIMESTAMP);

INSERT INTO crm_prosthetics (user_guid, device_id, model_name, last_service_date)
VALUES ('{{JOHN_DOE_UUID}}', 'DEV-BIO-999', 'Bionic-Hand-Ultra-v10', '2026-01-15');

-- Генерируем сырые тики активности протеза за 27 мая 2026 года
INSERT INTO telemetry_ticks (device_id, tick_timestamp, steps, active_seconds, battery_cycles)
VALUES 
('DEV-BIO-999', '2026-05-27 08:00:00', 1200, 1800, 42),
('DEV-BIO-999', '2026-05-27 14:30:00', 2500, 3600, 42),
('DEV-BIO-999', '2026-05-27 21:00:00', 800, 900, 43);