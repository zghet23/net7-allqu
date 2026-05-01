# LogGenerator — ECS Fargate Demo

Aplicación web en .NET 8 + Blazor Server diseñada para demostrar generación de logs estructurados y llamados a servicios externos. Pensada para ser desplegada en **AWS ECS Fargate** con integración a CloudWatch Logs.

---

## Características

- **GUI interactiva** con botones que disparan distintos niveles de log y escenarios de error
- **Logs en JSON estructurado** via Serilog, listos para ser ingestados por CloudWatch / Datadog / cualquier agente
- **Llamados a servicios externos reales** (Google, Amazon, YouTube, GitHub API, etc.) con medición de latencia
- **Escenarios de fallo simulados**: timeout, HTTP 500, DNS failure, retry con backoff exponencial
- **Health check** en `/health` compatible con ALB / ECS target group
- **Imagen Docker multi-stage**, usuario no-root, puerto configurable

---

## Stack

| Componente | Tecnología |
|---|---|
| Framework | .NET 8 / ASP.NET Core |
| UI | Blazor Server (InteractiveServer) |
| Logging | Serilog + `RenderedCompactJsonFormatter` |
| Resiliencia | Polly (retry con backoff exponencial) |
| HTTP | `HttpClient` con `IHttpClientFactory` |
| Health checks | `Microsoft.Extensions.Diagnostics.HealthChecks` |
| Contenedor | Docker multi-stage (SDK → ASP.NET runtime) |

---

## Estructura del proyecto

```
LogGenerator/
├── Components/
│   ├── Layout/                  # Layout general de la app
│   └── Pages/
│       └── Home.razor           # Página principal con toda la UI
├── Services/
│   ├── LogService.cs            # Gestión de logs + notificación reactiva a la UI
│   └── ExternalCallService.cs   # Llamados HTTP externos y escenarios de error
├── Program.cs                   # Bootstrap, Serilog, DI, health checks
├── Dockerfile                   # Imagen multi-stage para ECS Fargate
├── .dockerignore
└── appsettings.json
```

---

## Funcionalidades de la UI

### Generación de logs por nivel

| Botón | Nivel Serilog | Descripción |
|---|---|---|
| INFO | `Information` | Log informativo estándar |
| DEBUG | `Debug` | Detalle de diagnóstico verbose |
| WARNING | `Warning` | Advertencia, no bloquea el flujo |
| ERROR | `Error` | Condición de error simulada |
| CRITICAL | `Critical` | Evento crítico a nivel de sistema |
| EXCEPTION | `Error` | Lanza y captura una excepción real (`InvalidOperationException`) |
| BATCH (20 logs) | Mixto | Genera 20 logs ciclando entre los 4 niveles principales |

### Llamados a servicios externos

Cada llamado mide la latencia y loguea el resultado con status code HTTP.

| Servicio | Endpoint | Dato extra |
|---|---|---|
| Google | `https://www.google.com` | Latencia general |
| Amazon | `https://www.amazon.com` | Latencia + posibles redirects |
| YouTube | `https://www.youtube.com` | Latencia |
| GitHub API | `https://api.github.com/repos/dotnet/runtime` | Metadatos del repositorio |
| WorldTime | `https://worldtimeapi.org/api/timezone/UTC` | Hora del servidor remoto |
| IP Info | `https://ipinfo.io/json` | **IP pública del task de Fargate** |
| HTTPBin | `https://httpbin.org/get` | Headers que recibe el servidor |
| Ping TODOS | Todos los anteriores | Disparados en **paralelo** con `Task.WhenAll` |

### Escenarios de error

| Botón | Comportamiento | Log generado |
|---|---|---|
| Timeout (2s) | Llama a `httpbin.org/delay/10` con CancellationToken de 2s | `ERROR` — `TaskCanceledException` |
| HTTP 500 | Llama a `httpbin.org/status/500` | `ERROR` — HTTP 500 recibido |
| DNS Failure | Llama a un host inexistente `.invalid` | `ERROR` — `SocketException` |
| Retry con Backoff | Falla 2 veces intencionalmente, éxito en el 3er intento | `WARNING` x2 + `INFO` con resultado final |

---

## Formato de logs (stdout)

Todos los logs se escriben a `stdout` en formato **JSON compacto** (Serilog `RenderedCompactJsonFormatter`), compatible con CloudWatch Logs Insights, Datadog, y cualquier agente de log.

Ejemplo de un log de error:

```json
{
  "@t": "2026-05-01T22:15:03.1234560Z",
  "@m": "Exception in UI Button Click: InvalidOperationException",
  "@l": "Error",
  "@x": "System.InvalidOperationException: Simulated exception...",
  "app": "log-generator",
  "env": "production"
}
```

Campos fijos enriquecidos en todos los logs:

| Campo | Valor | Fuente |
|---|---|---|
| `app` | `log-generator` | `Program.cs` |
| `env` | Variable de entorno `ASPNETCORE_ENVIRONMENT` | Runtime |

---

## Correr localmente

**Prerequisito:** .NET 8 SDK instalado.

```bash
cd LogGenerator
dotnet run --urls "http://localhost:5055"
```

Abrir en el browser: `http://localhost:5055`

Health check: `http://localhost:5055/health` → responde `Healthy`

---

## Docker

### Build

```bash
cd LogGenerator
docker build -t log-generator .
```

### Run

```bash
docker run -p 8080:8080 log-generator
```

Abrir en el browser: `http://localhost:8080`

Health check: `http://localhost:8080/health`

### Variables de entorno disponibles

| Variable | Default | Descripción |
|---|---|---|
| `ASPNETCORE_URLS` | `http://+:8080` | Puerto de escucha |
| `ASPNETCORE_ENVIRONMENT` | `production` | Aparece en todos los logs como campo `env` |

---

## Despliegue en ECS Fargate

### Task Definition (puntos clave)

```json
{
  "containerDefinitions": [{
    "name": "log-generator",
    "image": "<account>.dkr.ecr.<region>.amazonaws.com/log-generator:latest",
    "portMappings": [{ "containerPort": 8080, "protocol": "tcp" }],
    "healthCheck": {
      "command": ["CMD-SHELL", "curl -f http://localhost:8080/health || exit 1"],
      "interval": 30,
      "timeout": 5,
      "retries": 3,
      "startPeriod": 10
    },
    "logConfiguration": {
      "logDriver": "awslogs",
      "options": {
        "awslogs-group": "/ecs/log-generator",
        "awslogs-region": "<region>",
        "awslogs-stream-prefix": "ecs"
      }
    },
    "environment": [
      { "name": "ASPNETCORE_ENVIRONMENT", "value": "production" }
    ]
  }]
}
```

### ALB Target Group

- **Protocol:** HTTP
- **Health check path:** `/health`
- **Healthy threshold:** 2
- **Unhealthy threshold:** 3
- **Timeout:** 5s
- **Interval:** 30s

---

## Arquitectura en AWS

```
Internet
    │
    ▼
Application Load Balancer (puerto 80/443)
    │
    ▼
ECS Fargate Task (puerto 8080)
    │  log-generator container
    │      │
    │      ├── /health  ←── ALB health check
    │      └── /        ←── Blazor UI
    │
    ▼
CloudWatch Logs
    └── /ecs/log-generator
            └── JSON estructurado por Serilog
```
