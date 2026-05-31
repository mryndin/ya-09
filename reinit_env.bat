@echo off
chcp 65001 > nul

:: ========================================================
:: НАСТРОЙКИ (Взяты строго из вашего docker-compose.yaml)
:: ========================================================
set "LDAP_PASS=adminpassword"

:: Точные имена контейнеров из container_name
set "LDAP_CONTAINER=openldap"
set "POSTGRES_CONTAINER=postgres-crm"
set "CLICKHOUSE_CONTAINER=clickhouse-server"

:: Настройки доступов к БД из окружения docker-compose
set "POSTGRES_USER=postgres_user"
set "POSTGRES_DB=crm_db"

set "CH_USER=ch_admin"
set "CH_PASSWORD=ch_password"
set "CH_DB=default"

:: Пути к файлам
set "LDAP_CONFIG=E:\work\Yandex\Sprints\ya-09\ldap\config.ldif"
set "PG_SCRIPT=postgres\init-crm.sql"
set "CH_SCRIPT=clickhouse\init.sql"

set "PG_TMP=postgres\init-crm.tmp.sql"
set "CH_TMP=clickhouse\init.tmp.sql"

echo ========================================================
echo Шаг 1: Перезапуск OpenLDAP и импорт записей (с пропуском дублей)
echo ========================================================
docker compose stop %LDAP_CONTAINER%
docker compose start %LDAP_CONTAINER%

echo Ожидаем готовность OpenLDAP (5 секунд)...
timeout /t 5 /nobreak > nul

if exist "%LDAP_CONFIG%" (
    docker exec -i %LDAP_CONTAINER% ldapadd -c -x -D "cn=admin,dc=example,dc=com" -w %LDAP_PASS% < "%LDAP_CONFIG%"
) else (
    echo [Ошибка] Файл конфигурации LDAP не найден по пути: %LDAP_CONFIG%
    pause
    exit /b
)

echo.
echo ========================================================
echo Шаг 2: Извлечение автосгенерированного entryUUID из LDAP
echo ========================================================
set "REAL_UUID="

docker exec -i %LDAP_CONTAINER% ldapsearch -x -D "cn=admin,dc=example,dc=com" -w %LDAP_PASS% -b "dc=example,dc=com" "(uid=john.doe)" entryUUID > ldap_out.tmp 2>nul

for /f "tokens=2" %%G in ('findstr "entryUUID:" ldap_out.tmp') do set "REAL_UUID=%%G"

if exist ldap_out.tmp del ldap_out.tmp

if not defined REAL_UUID (
    echo [КРИТИЧЕСКАЯ ОШИБКА] Не удалось получить entryUUID! 
    pause
    exit /b
)

set "REAL_UUID=%REAL_UUID:~0,36%"
echo [Успех] Текущий entryUUID для john.doe в LDAP: %REAL_UUID%

echo.
echo ========================================================
echo Шаг 3: Подготовка SQL-скриптов с реальным UUID
echo ========================================================
powershell -Command "(Get-Content '%PG_SCRIPT%' -Raw) -replace '{{JOHN_DOE_UUID}}', '%REAL_UUID%' | Set-Content '%PG_TMP%' -NoNewline"
powershell -Command "(Get-Content '%CH_SCRIPT%' -Raw) -replace '{{JOHN_DOE_UUID}}', '%REAL_UUID%' | Set-Content '%CH_TMP%' -NoNewline"

echo.
echo ========================================================
echo Шаг 4: Накат скриптов на Postgres и ClickHouse
echo ========================================================
if exist "%PG_TMP%" (
    docker exec -i %POSTGRES_CONTAINER% psql -U %POSTGRES_USER% -d %POSTGRES_DB% < "%PG_TMP%"
    echo [Успех] Скрипт успешно применен в контейнере %POSTGRES_CONTAINER%.
    del "%PG_TMP%"
) else (
    echo [Ошибка] Временный файл Postgres не создался.
)

if exist "%CH_TMP%" (
    docker exec -i %CLICKHOUSE_CONTAINER% clickhouse-client --user %CH_USER% --password %CH_PASSWORD% --database %CH_DB% --multiquery < "%CH_TMP%"
    echo [Успех] Скрипт успешно применен в контейнере %CLICKHOUSE_CONTAINER%.
    del "%CH_TMP%"
) else (
    echo [Ошибка] Временный файл ClickHouse не создался.
)

echo.
echo Все базы успешно синхронизированы по реальному entryUUID из LDAP!
pause