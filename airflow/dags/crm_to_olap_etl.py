import os
from datetime import datetime, timedelta
from airflow import DAG
from airflow.operators.python import PythonOperator
from airflow.providers.postgres.hooks.postgres import PostgresHook
from clickhouse_driver import Client

# Настройки по умолчанию для задач
default_args = {
    'owner': 'mryndin',
    'depends_on_past': False,
    'email_on_failure': False,
    'email_on_retry': False,
    'retries': 2,
    'retry_delay': timedelta(minutes=5),
}

def run_crm_to_clickhouse_etl(**context):
    """
    Основная логика ETL:
    1. Extract: Чтение инкремента из CRM (PostgreSQL)
    2. Transform: Агрегация телеметрии в разрезе клиентов
    3. Load: Пакетная вставка в ClickHouse OLAP
    """
    target_date = context['ds'] 
    
    print(f"Старт ETL-процесса за логическую дату: {target_date}")
    
    # Инициализируем хук для подключения к PostgreSQL
    pg_hook = PostgresHook(postgres_conn_id='postgres_crm_conn')
    
    # Инициализируем клиент ClickHouse
    ch_client = Client(
        host='clickhouse', 
        port=9000, 
        user='ch_admin', 
        password='ch_password', 
        database='default'
    )
    
    # 1. EXTRACT & TRANSFORM
    # Исправлено: u.id/user_id заменено на реальное поле user_guid из схемы crm_users
    extract_sql = f"""
        SELECT 
            u.user_guid,
            CAST('{target_date}' AS DATE) AS report_date,
            p.model_name AS model_name,
            COALESCE(SUM(t.steps), 0)::INTEGER AS steps_count,
            COALESCE(SUM(t.active_seconds) / 3600.0, 0.0)::REAL AS active_hours,
            COALESCE(MAX(t.battery_cycles), 0)::SMALLINT AS battery_cycles,
            p.last_service_date AS last_service_date,
            NOW()::TIMESTAMP AS sync_updated_at
        FROM crm_users u
        JOIN crm_prosthetics p ON u.user_guid = p.user_guid
        LEFT JOIN telemetry_ticks t ON p.device_id = t.device_id 
            AND t.tick_timestamp >= '{target_date} 00:00:00'
            AND t.tick_timestamp <= '{target_date} 23:59:59'
        WHERE t.device_id IS NOT NULL OR u.updated_at >= '{target_date} 00:00:00'
        GROUP BY u.user_guid, p.model_name, p.last_service_date;
    """
    
    print("Извлечение и агрегация данных из PostgreSQL...")
    records = pg_hook.get_records(extract_sql)
    
    if not records:
        print(f"За {target_date} новых данных или тиков телеметрии не обнаружено. Завершение.")
        return
        
    print(f"Извлечено {len(records)} строк для импорта.")
    
    # Преобразуем UUID в строку, так как clickhouse-driver ожидает строковый формат для типа UUID
    data_to_insert = [
        (
            str(row[0]), # user_guid (UUID -> str)
            row[1],      # report_date
            row[2],      # model_name
            row[3],      # steps_count
            row[4],      # active_hours
            row[5],      # battery_cycles
            row[6],      # last_service_date
            row[7]       # sync_updated_at
        )
        for row in records
    ]
    
    # 2. LOAD
    # Исправлено: имя колонки user_id заменено на user_guid согласно схеме таблицы ClickHouse
    print("Запись пакета данных в ClickHouse витрину v_user_prosthetic_reports...")
    insert_sql = """
        INSERT INTO default.v_user_prosthetic_reports (
            user_guid, report_date, model_name, steps_count, active_hours, battery_cycles, last_service_date, sync_updated_at
        ) VALUES
    """
    
    ch_client.execute(insert_sql, data_to_insert)
    print("ETL процесс успешно завершен!")


with DAG(
    'bionicpro_crm_to_olap_etl',
    default_args=default_args,
    description='Ежедневный инкрементальный сбор телеметрии CRM и IoT в ClickHouse',
    schedule_interval='0 2 * * *',
    start_date=datetime(2026, 5, 26),
    catchup=True,
    tags=['bionicpro', 'analytics'],
) as dag:

    run_etl = PythonOperator(
        task_id='execute_crm_to_olap_transform',
        python_callable=run_crm_to_clickhouse_etl,
        provide_context=True
    )

    run_etl