# ADR 001: Trust boundary между gateway, api и PostgreSQL

## Статус

Принято.

## Контекст

Клиент выбирает опубликованный action и передаёт payload, но не должен иметь возможности выбрать базу, schema, функцию, SQL, policy или предметный результат. Нужно зафиксировать, где именно проходит граница доверия и что проверяется на каждой стороне этой границы.

## Решение

Граница доверия проходит в двух точках:

1. **HTTP-граница (gateway → api).**
   Gateway не проверяет и не расшифровывает JWT — он прозрачно проксирует `Authorization`, заголовки и payload во внутренний `api` по Compose DNS, не логируя credentials и полный payload. Вся проверка подлинности происходит только в `api`.

2. **Внутри `api` (JwtContextMiddleware → JsonSchemaValidationMiddleware → executor).**
   - `api` проверяет подпись JWT (HS256, issuer/audience из конфигурации) и явно валидирует форму claims (`sub`, `consumer`, `scope`, `iat`, `exp`) — неверный тип claim даёт `401 auth.invalid`, а не framework-исключение.
   - Из проверенного токена формируется server-side context: `principal`, `consumer`, `scopes`. Эти поля **никогда** не берутся из payload — одноимённые поля в теле запроса не могут их подменить.
   - `correlationId` генерируется runtime как UUID на каждый HTTP-запрос; `requestId` = `Idempotency-Key`, если он передан; `deadline` вычисляется runtime из `timeout_ms` manifest.
   - Request schema проверяется до предметного вызова.

3. **Граница внутри PostgreSQL (`api.invoke`).**
   `api.invoke` — единственная точка предметного выполнения. Она повторно проверяет policy (scopes) по доверенному context, разрешает target function только из immutable action catalog и вызывает её с фиксированным `search_path`. Runtime-роль (`course_runtime`) имеет право выполнить `api.invoke`, но не имеет прямого DML-доступа к предметным таблицам — таким образом даже компрометация HTTP-уровня не даёт прямого доступа к данным в обход catalog.

## Последствия

- Policy проверяется дважды: на HTTP-границе (для быстрого отказа — `403 access.denied`) и повторно внутри `api.invoke` (не доверяя HTTP-уровню полностью).
- Ошибки никогда не раскрывают SQL, stack trace, connection string или внутренние имена target — единый error envelope это гарантирует на уровне контракта.
- Owner SECURITY DEFINER функций имеет `NOLOGIN` — даже при компрометации runtime-роли нельзя залогиниться под owner напрямую.

## Альтернативы, которые не выбраны

- Проверка policy только на HTTP-уровне — отклонено: `api.invoke` должна быть безопасна сама по себе, независимо от того, что происходит выше (defense in depth).
- Передача `principal`/`scopes` через payload вместо JWT-context — отклонено прямым требованием задания (клиент не выбирает context).
