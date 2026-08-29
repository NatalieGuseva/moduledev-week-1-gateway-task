-- ============================================================
-- Миграция 004: Defense-in-depth — запрет EXECUTE по умолчанию
-- ============================================================
--
-- PostgreSQL при CREATE FUNCTION по умолчанию выдаёт EXECUTE
-- роли PUBLIC. Это не нарушает требования contract-reference.md
-- (модель угроз явно исключает superuser и говорит про права
-- ролей на таблицах/views, а не про EXECUTE), но для полноты
-- defense-in-depth стоит явно ограничить: НИКАКАЯ роль не должна
-- иметь возможность вызвать функции схем api/course/opencheck
-- напрямую только потому, что у неё есть подключение к БД.
--
-- 1. Отзываем EXECUTE у PUBLIC на всех уже существующих функциях
--    в этих схемах (schema payment функций не содержит — только
--    таблицы, поэтому не упоминается ниже).
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA api FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA course FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA opencheck FROM PUBLIC;

-- 2. То же самое — по умолчанию для функций, которые будут
--    созданы в будущем в рамках этой же миграционной сессии/роли
--    (ALTER DEFAULT PRIVILEGES действует для объектов, создаваемых
--    ролью, выполняющей эту команду — тем же CLI-подключением,
--    которым применяются миграции).
ALTER DEFAULT PRIVILEGES IN SCHEMA api REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA course REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA opencheck REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

-- 3. Единственная точка входа для внешних вызовов — api.invoke.
--    Явно возвращаем EXECUTE только на неё для course_runtime
--    (роль, представляющая runtime-контекст приложения). Внутренние
--    course.*/opencheck.* функции course_runtime вызывать напрямую
--    не должен — они достижимы только изнутри api.invoke, которая
--    выполняется SECURITY DEFINER от имени владельца функции и не
--    зависит от EXECUTE-прав вызывающей роли на них.
GRANT EXECUTE ON FUNCTION api.invoke(TEXT, TEXT, INTEGER, JSONB, JSONB) TO course_runtime;
