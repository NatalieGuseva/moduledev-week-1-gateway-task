-- ============================================================
-- Миграция 010: Target функции для module_5abe942c84
-- ============================================================

-- Создаем схему
CREATE SCHEMA IF NOT EXISTS module_5abe942c84;

-- Target функция для action_5cd90325f0 v1
CREATE OR REPLACE FUNCTION module_5abe942c84.action_5cd90325f0_v1(
    p_context JSONB,
    p_payload JSONB
) RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = module_5abe942c84, course, api, public, pg_catalog
AS $$
DECLARE
    v_mode TEXT;
    v_value TEXT;
    v_correlation_id UUID;
    v_principal TEXT;
    v_request_id TEXT;
BEGIN
    v_correlation_id := COALESCE((p_context->>'correlationId')::UUID, gen_random_uuid());
    v_principal := p_context->>'principal';
    v_request_id := p_context->>'requestId';

    -- Извлекаем параметры из payload
    v_mode := p_payload->>'mode_d566e409';
    v_value := p_payload->>'value_c6e4ac8e';

    -- Проверка наличия обязательных полей
    IF v_mode IS NULL THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'payload.invalid',
            'message', 'Missing required field: mode_d566e409',
            'retryable', false
        );
    END IF;

    IF v_value IS NULL THEN
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'payload.invalid',
            'message', 'Missing required field: value_c6e4ac8e',
            'retryable', false
        );
    END IF;

    -- Логируем вызов (для отладки)
    RAISE DEBUG 'action_5cd90325f0_v1 called: mode=%, value=%, principal=%, correlationId=%',
        v_mode, v_value, v_principal, v_correlation_id;

    -- В зависимости от mode возвращаем разные outcomes
    IF v_mode = 'signal' THEN
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'ROUTE_SIGNAL_72125C4E',
            'result', jsonb_build_object(
                'stored', true,
                'mode', v_mode,
                'value', v_value,
                'correlationId', v_correlation_id
            )
        );
    ELSIF v_mode = 'manual' THEN
        RETURN jsonb_build_object(
            'status', 'ok',
            'outcome', 'ROUTE_MANUAL_503E0EA7',
            'result', jsonb_build_object(
                'stored', true,
                'mode', v_mode,
                'value', v_value,
                'correlationId', v_correlation_id
            )
        );
    ELSE
        RETURN jsonb_build_object(
            'status', 'error',
            'code', 'payload.invalid',
            'message', 'Invalid mode: ' || COALESCE(v_mode, 'null') || '. Expected "signal" or "manual"',
            'retryable', false
        );
    END IF;
END;
$$;

-- Отзываем права у PUBLIC
REVOKE ALL ON FUNCTION module_5abe942c84.action_5cd90325f0_v1 FROM PUBLIC;

-- Даем права workflow_worker
GRANT EXECUTE ON FUNCTION module_5abe942c84.action_5cd90325f0_v1 TO workflow_worker;

-- Даем права postgres (для миграций)
GRANT EXECUTE ON FUNCTION module_5abe942c84.action_5cd90325f0_v1 TO postgres;

-- Комментарий для документации
COMMENT ON FUNCTION module_5abe942c84.action_5cd90325f0_v1 IS
'Target function для action module_5abe942c84.action_5cd90325f0@1.
Поддерживает два режима:
- mode = "signal" → возвращает ROUTE_SIGNAL_72125C4E
- mode = "manual" → возвращает ROUTE_MANUAL_503E0EA7
Используется в workflow-карте flow-4a31f70549 v1.';