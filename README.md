# Неделя 1. Database-first action runtime

Решение задания ModuleDev — Неделя 1: C# gateway и generic action runtime, публикующий зарегистрированные PostgreSQL-функции как HTTP actions.

## Решение

### Архитектура

Решение состоит из четырёх сервисов в Docker Compose:

- **gateway** (ASP.NET Core, YARP) — единственная точка входа снаружи, публикует host-порт `8080`. Проксирует запросы во внутренний `api` по Compose DNS (`http://api:8080`), не содержит предметной логики и не имеет доступа к PostgreSQL.
- **api** — внутренний action runtime без опубликованных host-портов. Выполняет проверку JWT, формирует доверенный context, валидирует request/response schema и вызывает `api.invoke(...)` в одной Npgsql transaction. Запускается только после того, как `cli` успешно применит миграции (`service_completed_successfully`).
- **cli** — Course CLI. При `docker compose up` по умолчанию выполняет `migration apply` и завершается — миграции применяются автоматически при каждом чистом запуске. Для остальных команд (публикация/активация/отключение actions) запускается вручную через `docker compose run --rm cli ...`, при этом переопределяет команду по умолчанию. Пишет в stdout ровно один JSON-документ (envelope `status: ok|error`), диагностика — в stderr.
- **postgres** — PostgreSQL 17, авторитетное состояние (схемы `course`, `api`, `autocheck`), данные хранятся в named volume `course_pgdata` и переживают пересоздание `gateway`/`api`/`cli`.

Направление вызовов: клиент → `gateway:8080` → `api` (internal, Compose DNS) → JWT + context → resolve action manifest → Npgsql transaction → `api.invoke(...)` → зарегистрированная PostgreSQL-функция → commit/rollback.

Диаграмма: [C4 Container diagram](docs/c4-container.md)

### Запуск

**Требования:** Docker Desktop с Compose v2.20+ (нужна поддержка `service_completed_successfully`).

```bash
docker compose up -d --build
```

Никаких ручных SQL-команд, `.env` или публикации встроенных actions после чистого запуска не требуется — в `docker-compose.yml` уже заданы безопасные значения по умолчанию (`POSTGRES_PASSWORD`, `COURSE_JWT_*`) для локальной разработки и проверки. `cli` применяет миграции автоматически, `api` стартует только после их успешного завершения.

Проверка доступности:

```bash
curl http://localhost:8080/health/live
curl http://localhost:8080/health/ready
```

#### Переопределение конфигурации (опционально)

Значения по умолчанию подходят для локального запуска и автопроверки "как есть". Если нужны свои — создайте `.env` в корне проекта (файл не коммитится в Git):

```bash
POSTGRES_USER=postgres
POSTGRES_PASSWORD=your_password_here
POSTGRES_DB=course
COURSE_JWT_ISSUER=moduledev-course
COURSE_JWT_AUDIENCE=moduledev-api
COURSE_JWT_SIGNING_KEY=your_signing_key_here_at_least_32_chars
```

Проверка курса подставляет собственный `COURSE_JWT_SIGNING_KEY` через Compose override — значение из `.env` для неё не используется.
### Конфигурация

`api` и `cli` читают переменные окружения. В `docker-compose.yml` для всех них заданы безопасные значения по умолчанию для локального запуска и автопроверки; `.env` нужен только если хотите их переопределить (реальные секреты в репозиторий не попадают):

| Переменная | Назначение |
|---|---|
| `COURSE_JWT_ISSUER` | issuer для проверки JWT (`moduledev-course`) |
| `COURSE_JWT_AUDIENCE` | audience для проверки JWT (`moduledev-api`) |
| `COURSE_JWT_SIGNING_KEY` | ключ подписи HS256, ≥ 32 байт |
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` | параметры подключения к PostgreSQL |
| `ConnectionStrings__CourseDb` | строка подключения `api`/`cli` к PostgreSQL |

`.env` с реальными секретами не входит в Git.

### Миграции

SQL-миграции лежат в `Api/Migrations/ChecksummedMigrations/` и применяются сервисом `cli`:

- **Автоматически** — при каждом `docker compose up` (в том числе `--force-recreate`), до старта `api`.
- **Вручную**, при необходимости повторного прогона на уже поднятом стеке:

```bash
docker compose run --rm cli migration apply /app/Migrations/ChecksummedMigrations
```

Миграции выполняются в лексикографическом порядке файлов, каждая — в отдельной транзакции. Применённые файлы фиксируются по SHA-256 checksum в `course.schema_migrations`: повтор с тем же содержимым безопасен (skip), изменение уже применённого файла возвращает `manifest.conflict`. `api` migration credentials не использует.

### Проверка

Важно: перед запуском проверки обязательно освободите порт и очистите состояние предыдущего запуска:

```bash
docker compose down -v
./check.sh
```

Результат записывается в `week-1-public-report.json`.

### Диагностика

```bash
docker compose logs gateway
docker compose logs api
docker compose logs cli
docker compose logs postgres

curl http://localhost:8080/health/live
curl http://localhost:8080/health/ready
curl http://localhost:8080/openapi/default.json
curl http://localhost:8080/openapi/actions/payment/request/1.json
```

Подключение к БД для Windows (Git Bash):

```bash
winpty docker compose exec postgres psql -U postgres -d course
```

### Ограничения

- На первой неделе `process_id` в `operations` всегда `null`, workflow ещё не реализован.
- Поддерживается только валюта `RUB`.
- Только `POST` в обязательной части action manifest.
- Rate limiting, CORS-трансформации и защитные очереди gateway не входят в неделю 1.


ADR:
- [ADR 001: Trust boundary](docs/001-trust-boundary.md)
- [ADR 002: Технический и предметный результат](docs/002-technical-vs-domain-result.md)