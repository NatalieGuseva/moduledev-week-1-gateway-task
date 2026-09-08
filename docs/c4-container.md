# C4 Container diagram

Database-first action runtime — контейнерная диаграмма (уровень C4 Container).

```mermaid
C4Container
    title Container diagram — ModuleDev Week 1: Database-first action runtime

    Person(client, "Клиент", "Внешний потребитель API (candidate-client / workflow-worker / reviewer)")

    System_Boundary(system, "Database-first action runtime") {
        Container(gateway, "Gateway", "ASP.NET Core, YARP", "Единственный внешний entrypoint на :8080. Проксирует запросы, whitelist routes, не содержит предметной логики")
        Container(api, "Api", "ASP.NET Core", "Internal action runtime. JWT + context, schema validation, generic HTTP executor, Npgsql transaction")
        Container(cli, "Cli", "Console app (.NET)", "Course CLI: migrations, publish/activate/disable actions. JSON envelope в stdout")
        ContainerDb(postgres, "PostgreSQL", "PostgreSQL 17", "Схемы course (catalog, operations, events), api (invoke), autocheck (read-only views)")
    }

    Rel(client, gateway, "HTTPS/JSON", "POST /api/{module}/{action}, X-Action-Version, Idempotency-Key")
    Rel(gateway, api, "HTTP, Compose DNS", "проксирует запрос и заголовки без изменения смысла")
    Rel(api, postgres, "Npgsql, single transaction", "SELECT manifest, api.invoke(...), commit/rollback")
    Rel(cli, postgres, "Npgsql", "migration apply, INSERT/UPDATE action_catalog")

    UpdateRelStyle(client, gateway, $offsetY="-10")
    UpdateRelStyle(gateway, api, $offsetY="-10")
```

## Пояснения к связям

| Связь | Что проверяется/гарантируется |
|---|---|
| Client → Gateway | единственная публичная точка входа (`:8080`); JWT/credentials/payload не логируются |
| Gateway → Api | Compose DNS (`http://api:8080`), не `localhost`; gateway не видит catalog и PostgreSQL |
| Api → PostgreSQL | одна Npgsql transaction на HTTP-запрос; `api.invoke` — единственная точка предметного выполнения; runtime-роль без прямого DML к предметным таблицам |
| Cli → PostgreSQL | публикует/активирует actions и применяет миграции; использует отдельную (publication) роль, не runtime-роль `api` |

Если нужен PNG/SVG — этот mermaid-блок рендерится в GitHub/GitLab автоматически, либо экспортируется через `mmdc` (mermaid-cli) или https://mermaid.live.
