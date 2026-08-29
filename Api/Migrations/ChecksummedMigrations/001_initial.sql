-- ============================================================
-- Миграция 001: Базовая структура базы данных
-- ============================================================

-- 1. Создание схем
CREATE SCHEMA IF NOT EXISTS course;
CREATE SCHEMA IF NOT EXISTS autocheck;
CREATE SCHEMA IF NOT EXISTS api;
CREATE SCHEMA IF NOT EXISTS opencheck;
CREATE SCHEMA IF NOT EXISTS payment;

-- 2. Создание ролей (идемпотентно — безопасно перезапускать)
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'course_runtime') THEN
        CREATE ROLE course_runtime NOLOGIN;
    END IF;
END
$$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'course_owner') THEN
        CREATE ROLE course_owner NOLOGIN;
    END IF;
END
$$;

-- 3. Включение расширения для хеширования
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- 4. Таблица action_catalog (хранилище манифестов)
CREATE TABLE IF NOT EXISTS course.action_catalog (
    module TEXT NOT NULL,
    action TEXT NOT NULL,
    version INTEGER NOT NULL,
    http_method TEXT NOT NULL DEFAULT 'POST',
    target_schema TEXT NOT NULL,
    target_function TEXT NOT NULL,
    request_schema JSONB NOT NULL,
    response_schema JSONB NOT NULL,
    outcomes JSONB NOT NULL,
    required_policy JSONB NOT NULL,
    idempotency_mode TEXT NOT NULL,
    idempotency_scope TEXT NOT NULL,
    timeout_ms INTEGER NOT NULL DEFAULT 30000,
    enabled BOOLEAN NOT NULL DEFAULT FALSE,
    is_default BOOLEAN NOT NULL DEFAULT FALSE,
    published_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (module, action, version)
);

-- 5. Уникальный индекс для default версий
CREATE UNIQUE INDEX IF NOT EXISTS idx_action_catalog_default
    ON course.action_catalog (module, action)
    WHERE is_default = TRUE AND enabled = TRUE;

-- 6. Функция для авто-включения первой версии при вставке
CREATE OR REPLACE FUNCTION course.auto_enable_first_version()
RETURNS TRIGGER AS $$
BEGIN
    -- Проверяем, есть ли уже другие версии этого action
    IF NOT EXISTS (
        SELECT 1 FROM course.action_catalog
        WHERE module = NEW.module AND action = NEW.action
          AND (module, action, version) <> (NEW.module, NEW.action, NEW.version)
    ) THEN
        -- Это первая версия — включаем её
        NEW.enabled := TRUE;
        NEW.is_default := TRUE;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- 7. Триггер на вставку в action_catalog
DROP TRIGGER IF EXISTS trg_auto_enable_first_version ON course.action_catalog;
CREATE TRIGGER trg_auto_enable_first_version
BEFORE INSERT ON course.action_catalog
FOR EACH ROW
EXECUTE FUNCTION course.auto_enable_first_version();

-- 8. Таблица operations (предметные операции)
CREATE TABLE IF NOT EXISTS course.operations (
    operation_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id TEXT NOT NULL,
    principal TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    operation_kind TEXT NOT NULL,
    amount NUMERIC(19,2) NOT NULL,
    currency TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('CREATED', 'PROCESSING', 'COMPLETED', 'REJECTED')),
    process_id UUID,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT operations_idempotency_key UNIQUE (principal, request_id)
);

-- 9. Таблица operation_events (история событий)
CREATE TABLE IF NOT EXISTS course.operation_events (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    operation_id UUID NOT NULL REFERENCES course.operations(operation_id) ON DELETE CASCADE,
    event_type TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    occurred_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- 10. Таблица action_dispatches (журнал вызовов)
CREATE TABLE IF NOT EXISTS course.action_dispatches (
    correlation_id UUID NOT NULL,
    request_id TEXT,
    module TEXT NOT NULL,
    action TEXT NOT NULL,
    version INTEGER NOT NULL,
    principal TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('OK', 'ERROR')),
    outcome TEXT,
    occurred_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (correlation_id)
);

-- 11. Таблица idempotency_records (для идемпотентности на уровне API)
CREATE TABLE IF NOT EXISTS course.idempotency_records (
    id SERIAL PRIMARY KEY,
    idempotency_key TEXT NOT NULL,
    module TEXT NOT NULL,
    action TEXT NOT NULL,
    version INTEGER NOT NULL,
    principal TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    result JSONB NOT NULL,
    status TEXT NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(idempotency_key, module, action, version, principal)
);

-- 12. Views для autocheck
-- 12.1 contract_info
CREATE OR REPLACE VIEW autocheck.contract_info AS
SELECT
    'course-1' AS contract_version,
    CURRENT_TIMESTAMP AS generated_at;

-- 12.2 action_definitions
CREATE OR REPLACE VIEW autocheck.action_definitions AS
SELECT
    module,
    action,
    version,
    http_method,
    target_schema,
    target_function,
    outcomes,
    required_policy,
    enabled,
    is_default
FROM course.action_catalog;

-- 12.3 action_dispatches
CREATE OR REPLACE VIEW autocheck.action_dispatches AS
SELECT
    correlation_id,
    request_id,
    module,
    action,
    version,
    principal,
    payload_hash,
    status,
    outcome,
    occurred_at
FROM course.action_dispatches;

-- 12.4 operations
CREATE OR REPLACE VIEW autocheck.operations AS
SELECT
    operation_id,
    request_id,
    operation_kind,
    amount,
    currency,
    status,
    process_id,
    created_at,
    updated_at
FROM course.operations;

-- 12.5 operation_events
CREATE OR REPLACE VIEW autocheck.operation_events AS
SELECT
    event_id,
    operation_id,
    event_type,
    payload_hash,
    occurred_at
FROM course.operation_events;

-- 13. Настройка прав
GRANT USAGE ON SCHEMA autocheck TO course_runtime;
GRANT SELECT ON ALL TABLES IN SCHEMA autocheck TO course_runtime;

GRANT USAGE ON SCHEMA api TO course_runtime;

GRANT USAGE ON SCHEMA course TO course_owner;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA course TO course_owner;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA course TO course_owner;

REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA course FROM course_runtime;
REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA course FROM course_runtime;

GRANT USAGE ON SCHEMA course TO course_runtime;
GRANT SELECT ON course.action_catalog TO course_runtime;
GRANT SELECT, INSERT ON course.idempotency_records TO course_runtime;
GRANT USAGE, SELECT ON SEQUENCE course.idempotency_records_id_seq TO course_runtime;

-- Права на схемы opencheck и payment
GRANT USAGE ON SCHEMA opencheck TO course_runtime;
GRANT USAGE ON SCHEMA payment TO course_runtime;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA opencheck TO course_owner;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA payment TO course_owner;

ALTER SCHEMA course OWNER TO course_owner;
ALTER SCHEMA api OWNER TO course_owner;
ALTER SCHEMA autocheck OWNER TO course_owner;
ALTER SCHEMA opencheck OWNER TO course_owner;
ALTER SCHEMA payment OWNER TO course_owner;

ALTER TABLE course.action_catalog OWNER TO course_owner;
ALTER TABLE course.operations OWNER TO course_owner;
ALTER TABLE course.operation_events OWNER TO course_owner;
ALTER TABLE course.action_dispatches OWNER TO course_owner;
ALTER TABLE course.idempotency_records OWNER TO course_owner;

ALTER VIEW autocheck.contract_info OWNER TO course_owner;
ALTER VIEW autocheck.action_definitions OWNER TO course_owner;
ALTER VIEW autocheck.action_dispatches OWNER TO course_owner;
ALTER VIEW autocheck.operations OWNER TO course_owner;
ALTER VIEW autocheck.operation_events OWNER TO course_owner;

COMMENT ON TABLE course.action_catalog IS 'Immutable action catalog with published manifests';
COMMENT ON TABLE course.operations IS 'Business operations with idempotency protection';
COMMENT ON TABLE course.operation_events IS 'Append-only event history for operations';
COMMENT ON TABLE course.action_dispatches IS 'Log of all action invocations';
COMMENT ON TABLE course.idempotency_records IS 'Idempotency records for API-level deduplication';
COMMENT ON VIEW autocheck.contract_info IS 'Contract version info for autocheck';
COMMENT ON VIEW autocheck.action_definitions IS 'Enabled action definitions for autocheck';
COMMENT ON VIEW autocheck.action_dispatches IS 'Action invocation log for autocheck';
COMMENT ON VIEW autocheck.operations IS 'Operations view for autocheck';
COMMENT ON VIEW autocheck.operation_events IS 'Operation events view for autocheck';