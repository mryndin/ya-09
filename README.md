# Проектная работа 9 спринта – BionicPRO

В рамках спринта были решены задачи повышения безопасности системы, разработан сервис отчётов, оптимизирована нагрузка на базу данных и внедрён CDC для стабильной работы CRM.  
Все изменения задокументированы скриншотами, которые находятся в папке `screenshots/`.

---

## 1. Повышение безопасности системы

### 1.1. Архитектурное решение для управления учётными данными (C4)

![C4-диаграмма контейнеров BionicPRO](Tasks/01/BionicPRO_C4_model.drawio.png)

На диаграмме отражены:
- **Унификация доступа** через сервис `bionicpro-auth` (BFF), скрывающий токены IdP от фронтенда.
- **Локальное хранение данных** – разделение по странам представительств.
- **Безопасная схема работы с токенами** – фронтенд получает только сессионную HttpOnly cookie.
- **Поддержка внешних удостоверяющих служб** (LDAP, Яндекс ID) через Keycloak Identity Brokering.

---

### 1.2. Замена Code Grant на PKCE (Proof Key for Code Exchange)

Безопасность аутентификации усилена переходом на Authorization Code Flow с PKCE (метод S256).  
**Настройки клиента `reports-frontend` в Keycloak:**

![PKCE Settings](screenshots/1.1.1.png)  
*Вкладка Settings – включён Standard Flow.*

![PKCE Advanced](screenshots/1.1.2.png)  
*Вкладка Advanced – Proof Key for Code Exchange (PKCE) Code Challenge Method = S256.*

![LDAP_ID Mapping](screenshots/1.1.3.png)  
*Маппинг LDAP_ID в Client Scopes для передачи идентификатора из LDAP.*

**Результат:**  
- При переходе на `/api/auth/login` браузер перенаправляется на Keycloak с параметрами `code_challenge` и `code_challenge_method=S256`.  
- После аутентификации `bionicpro-auth` обменивает код с `code_verifier` на токены и устанавливает сессионную cookie `BIONIC_SESSION`.  
- Попытка использовать неверный `code_verifier` приводит к ошибке `invalid_grant` (PKCE verification failed).

![Login Page](screenshots/1.5.1_login_page.png)  
*Страница входа в Keycloak (начала PKCE-потока).*

---

### 1.3. Безопасное получение и хранение токенов (bionicpro-auth)

- **Время жизни access-токена** сокращено до 2 минут:  
  ![Access Token Lifespan](screenshots/1.3.1_access_token_lifespan.png)

- **Сервис `bionicpro-auth`** (BFF) выполняет обмен кода на токены, сохраняет `refresh_token` в зашифрованном кэше, а `access_token` – в памяти с привязкой к сессии. Фронтенду передаётся только сессионная cookie с флагами `HttpOnly`, `Secure`, `SameSite=Lax`.

- **Ротация сессии** реализована в `ProxyController`: при каждом запросе сессия обновляется, и клиент получает новую cookie, что предотвращает session fixation.

- **Автоматическое обновление токенов**: если `access_token` истёк, BFF самостоятельно обновляет его через `refresh_token` и перевыпускает сессию.

*(Код сервиса `bionicpro-auth` и контроллеров `AuthController`, `ProxyController` представлен в репозитории.)*

---

### 1.4. Интеграция с LDAP

Для поддержки представительств в других странах развёрнут OpenLDAP и настроена федерация в Keycloak.

![LDAP Settings](screenshots/1.4.1_ldap_settings.png)  
*Параметры подключения к LDAP-серверу.*

![LDAP Mappers](screenshots/1.4.2_ldap_mappers.png)  
*Маппинг LDAP-групп на роли Keycloak.*

![User Attributes](screenshots/1.4.3_user_attributes.png)  
*Атрибуты пользователя, полученные из LDAP.*

![LDAP Attributes](screenshots/1.4.4_user_ldap_attributes.png)  
*Дополнительные LDAP-атрибуты (включая LDAP_ID).*

---

### 1.5. Многофакторная аутентификация (OTP)

В Keycloak активирован обязательный OTP для всех пользователей.

![OTP Browser Flow](screenshots/1.5.1_otp_browser_flow.png)  
*В браузерный flow добавлен обязательный шаг OTP Form.*

![User OTP Configured](screenshots/1.5.2_user_otp_configured.png)  
*У пользователя настроен OTP (Credentials).*

![OTP Input Page](screenshots/1.5.3_otp_input_page.png)  
*Страница ввода одноразового кода из Google Authenticator.*

---

### 1.6. Яндекс ID (Identity Brokering)

Реализован вход через Яндекс ID с запросом разрешения на использование данных профиля.

![Yandex IDP Settings](screenshots/1.6.1_yandex_idp_settings.png)  
*Настройки провайдера Яндекс ID в Keycloak.*

После аутентификации сервис получает и сохраняет данные профиля пользователя (имя, email).

---

## 2. Разработка сервиса отчётов

### 2.1. Архитектура решения

Итоговая архитектура включает ETL-процесс на Apache Airflow, витрину в ClickHouse и API-сервис `bionicpro-analytics`.  
*(Общая диаграмма контейнеров из п. 1.1 также охватывает этот блок.)*

---

### 2.2. ETL-процесс в Airflow

DAG `bionicpro_crm_to_olap_etl` регулярно извлекает данные из CRM, загружает в ClickHouse и формирует витрину.

![Airflow DAG List](screenshots/2.2.1_airflow_dag_list.png)  
*Список DAG-ов с успешными запусками.*

![Airflow DAG Runs](screenshots/2.2.2_airflow_dag_runs.png)  
*Сетка выполнения DAG (Grid view), зелёные квадраты – успешные запуски.*

![Airflow Schedule](screenshots/2.2.3_airflow_schedule.png)  
*Расписание запуска DAG (`@daily`).*

---

### 2.3. Данные в витрине ClickHouse

После ETL-обработки данные телеметрии и CRM объединяются в таблице `v_user_prosthetic_reports_cdc`.

![ClickHouse Vitrina Data](screenshots/2.2.5_clickhouse_vitrina_data.png)  
*Выборка из витрины с агрегированными данными по пользователю.*

---

### 2.4. Доступ к отчётам и ограничения

- **API** `bionicpro-analytics` предоставляет эндпоинт `/api/internal/reports`, принимающий `X-User-Id` и возвращающий JSON со ссылкой на сгенерированный отчёт.
- **Проверка прав:** `bionicpro-auth` извлекает `user_id` из JWT, подставляет в заголовок `X-User-Id`, гарантируя, что пользователь получит только свои данные. Запрос без сессионной cookie или с чужой сессией приводит к `401`/`403`.
- **Ограничение периода:** нельзя запросить данные за будущие даты или за ещё не обработанный Airflow период (проверка `maxAllowedDate`).

---

### 2.5. Пользовательский интерфейс

На фронтенде (`http://localhost:3000`) реализована форма выбора дат и кнопка «Download Excel Report».

![Frontend Report Page](screenshots/2.5.1_frontend_report_page.png)  
*Страница с формой запроса отчёта.*

![Downloaded Report](screenshots/2.5.2_report_downloaded.png)  
*Полученный Excel-файл с данными о работе протеза.*

---

## 3. Снижение нагрузки на базу данных (S3 + CDN)

### 3.1. Архитектура кэширования и хранения

![C4 с S3 и CDN](Tasks/03/BionicPRO_C4_model.drawio.png)  
*Диаграмма контейнеров, включающая MinIO (S3) и Nginx (CDN).*

![Flow S3-CDN](Tasks/03/Flow.drawio.png)  
*Блок-схема запроса отчёта: проверка в S3 → генерация при необходимости → сохранение в S3 → отдача через CDN.*

---

### 3.2. Хранилище MinIO (S3)

Все сформированные отчёты сохраняются в бакете `bionicpro-reports` с ключом `reports/{user_id}/...`.

![MinIO Full Structure](screenshots/3.1_minio_full_structure.png)  
*Структура бакета в MinIO с папками пользователей и файлами отчётов.*

---

### 3.3. Кэширование через CDN (Nginx)

- Nginx настроен как reverse proxy с кэшированием статических файлов.
- При первом запросе файл отдаётся напрямую из MinIO, Nginx сохраняет его в кэш (`X-Cache-Status: MISS`). При повторном запросе – отдача из кэша (`X-Cache-Status: HIT`), что разгружает ClickHouse и S3.

*(Конфигурация Nginx и лог cache-статусов могут быть продемонстрированы дополнительно.)*

---

## 4. Повышение оперативности и стабильности CRM (CDC)

### 4.1. Debezium и PostgreSQL

Настроен Change Data Capture для таблицы `public.crm_prosthetics` с помощью Debezium.

![Debezium Connectors](screenshots/4.1.1_debezium_connectors.png)  
*Список зарегистрированных коннекторов.*

![Debezium Status](screenshots/4.1.2_debezium_status.png)  
*Статус коннектора `crm-cdc-connector` – RUNNING.*

---

### 4.2. Интеграция с Kafka

Debezium публикует изменения в топик Kafka `crm_prod.public.crm_prosthetics`.

![Kafka Engine Table](screenshots/4.3.1_clickhouse_kafka_engine.png)  
*Определение таблицы `kafka_prosthetic_reports_queue` с движком Kafka.*

![Kafka MV](screenshots/4.3.2_clickhouse_kafka_mv.png)  
*Материализованное представление `mv_kafka_to_prosthetic_reports`, переносящее данные из Kafka в витрину.*

---

### 4.3. Витрина в ClickHouse на основе CDC

Итоговая витрина `v_user_prosthetic_reports_cdc` построена на движке `ReplacingMergeTree` и содержит актуальные данные из CRM, обновляемые в реальном времени.

![CDC Materialized View](screenshots/4.4.1_clickhouse_materialized_view.png)  
*Определение витрины, используемой сервисом аналитики.*

---

## Заключение

Все поставленные задачи выполнены:
- Безопасность усилена (PKCE, BFF, OTP, федерация с LDAP и Яндекс ID).
- Реализован сервис отчётов с генерацией Excel-файлов через Airflow и ClickHouse.
- Внедрено кэширование отчётов в S3 и CDN для снижения нагрузки.
- Настроен CDC на базе Debezium + Kafka + ClickHouse для стабильной работы CRM.

Скриншоты всех ключевых компонентов, настроек и интерфейсов представлены в папке `screenshots/`.