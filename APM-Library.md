# Datadog APM — .NET Tracer en ECS Fargate

## Contexto

El sidecar del agente de Datadog maneja métricas de infraestructura. Para APM (trazas de requests HTTP, spans de HttpClient, etc.) se necesita un segundo componente: el **tracer de .NET**, que vive **dentro de tu imagen de aplicación**.

El tracer se instala como un **profiler nativo del CLR** — se engancha al runtime de .NET antes de que tu código arranque, sin modificar nada de la lógica de negocio.

---

## Paso a paso: instalar el tracer en el Dockerfile

### 1. Descargar el tarball

Ir a: `https://github.com/DataDog/dd-trace-dotnet/releases`

Descargar: `datadog-dotnet-apm-X.X.X.tar.gz`
(tarball genérico para Linux x64 — no el `.deb` ni el `.rpm`)

Colocarlo en el mismo directorio que tu `Dockerfile`.

### 2. Agregar al stage `runtime` del Dockerfile

```dockerfile
# Copia el tarball al contenedor
COPY datadog-dotnet-apm-3.42.0.tar.gz /tmp/dd-tracer.tar.gz

# Extrae el tracer — este paso es el que muchos olvidan
RUN mkdir -p /opt/datadog && \
    tar -xz -C /opt/datadog -f /tmp/dd-tracer.tar.gz && \
    rm /tmp/dd-tracer.tar.gz

# Le dices al CLR de .NET que active el profiler
ENV CORECLR_ENABLE_PROFILING=1
ENV CORECLR_PROFILER={846F5F1C-F9AE-4B07-969E-05C26BC060D8}
ENV CORECLR_PROFILER_PATH=/opt/datadog/Datadog.Trace.ClrProfiler.Native.so
ENV DD_DOTNET_TRACER_HOME=/opt/datadog
```

> **Error común:** copiar el tarball y olvidar el `RUN tar`. El archivo llega al contenedor pero nunca se desempaca. El CLR busca el `.so` en `/opt/datadog/`, no lo encuentra, y silenciosamente ignora el profiler. La app corre normal pero sin APM.

---

## Qué hace cada variable CLR

| Variable | Para qué sirve |
|---|---|
| `CORECLR_ENABLE_PROFILING=1` | Le dice al runtime de .NET que active un profiler |
| `CORECLR_PROFILER={846F5F1C...}` | El GUID que identifica el profiler de Datadog ante el CLR |
| `CORECLR_PROFILER_PATH` | Ruta al `.so` nativo que se inyecta en el proceso |
| `DD_DOTNET_TRACER_HOME` | Directorio base donde el tracer busca sus archivos de configuración y managed DLLs |

Estas cuatro variables juntas habilitan la auto-instrumentación: intercepta HttpClient, ASP.NET Core middleware y más — sin tocar el código de la aplicación.

---

## Variables de entorno por contenedor

### `net-app` (tu aplicación)

| Variable | Valor | Para qué sirve |
|---|---|---|
| `CORECLR_ENABLE_PROFILING` | `1` | Activa el profiler en el CLR |
| `CORECLR_PROFILER` | `{846F5F1C-F9AE-4B07-969E-05C26BC060D8}` | GUID del profiler de Datadog |
| `CORECLR_PROFILER_PATH` | `/opt/datadog/Datadog.Trace.ClrProfiler.Native.so` | Ruta al profiler nativo |
| `DD_DOTNET_TRACER_HOME` | `/opt/datadog` | Directorio home del tracer |
| `DD_AGENT_HOST` | `127.0.0.1` | Dirección del agente (mismo task = misma red en `awsvpc`) |
| `DD_TRACE_AGENT_PORT` | `8126` | Puerto APM del agente |
| `DD_SERVICE` | `netapp` | Nombre del servicio en Datadog |
| `DD_ENV` | `dev` | Ambiente |
| `DD_VERSION` | `1.0.0` | Versión para Unified Service Tagging |

### `datadog-agent`

| Variable | Valor | Para qué sirve |
|---|---|---|
| `DD_API_KEY` | *(desde Secrets Manager)* | Autenticación con Datadog |
| `DD_SITE` | `datadoghq.com` | Endpoint de ingestión |
| `DD_APM_ENABLED` | `true` | Activa el receptor de trazas en el agente |
| `DD_APM_NON_LOCAL_TRAFFIC` | `true` | Acepta trazas desde otros contenedores del task |
| `DD_ENV` | `dev` | Etiqueta el ambiente en las métricas del agente |
| `ECS_FARGATE` | `true` | Le dice al agente que use la metadata API de Fargate en vez del Docker socket |
| `DD_ECS_COLLECT_RESOURCE_TAGS_EC2` | `true` | Para que CCM correlacione costos del CUR con tareas que reporta el Agent |
| `DD_ECS_TASK_COLLECTION_ENABLED` | `true` | Para habilitar o deshabilitar la recopilación automática de metadatos de las tareas de Amazon ECS  |

---

## Verificar que APM funciona

Una vez que el nuevo task esté corriendo, revisar los logs del contenedor de la app en CloudWatch. Un arranque exitoso del tracer se ve así:

```
[dd:info] DATADOG TRACER CONFIGURATION - {"agent_url":"http://127.0.0.1:8126", "service":"netapp", ...}
```

Esa línea confirma que el profiler del CLR cargó correctamente. Después de generar tráfico, el servicio aparece en **Datadog → APM → Services**.

---

## Unified Service Tagging — Docker Labels

### Qué son y para qué sirven

El agente de Datadog recolecta métricas de infraestructura (CPU, memoria) leyendo la metadata del task de ECS Fargate. Para saber a qué servicio pertenece cada contenedor, el agente no lee las variables de entorno — lee los **Docker labels** del contenedor.

Sin estos labels, el agente ve el contenedor pero no puede correlacionarlo con el servicio APM. Datadog muestra el mensaje:
> *"Metrics are estimates because unified service tagging isn't enabled."*

Con los labels, las métricas de infraestructura quedan taggeadas con `service`, `env` y `version`, enlazándolas exactamente con las trazas y logs del mismo servicio.

### Por qué no alcanzan las variables de entorno

| Mecanismo | Quién lo lee | Para qué |
|---|---|---|
| `DD_SERVICE` / `DD_ENV` / `DD_VERSION` (env vars) | El tracer de .NET (CLR profiler) | Taggear trazas APM y logs |
| `com.datadoghq.tags.*` (Docker labels) | El agente de Datadog | Taggear métricas de infraestructura del contenedor |

Son dos canales distintos. Uno no reemplaza al otro — ambos son necesarios para tener correlación completa.

### Dónde se configuran

En la task definition de ECS, dentro de la definición del contenedor `net-app`, como campo `dockerLabels`:

```json
"dockerLabels": {
  "com.datadoghq.tags.service": "netapp",
  "com.datadoghq.tags.env":     "dev",
  "com.datadoghq.tags.version": "2.0"
}
```

Los valores deben coincidir exactamente con `DD_SERVICE`, `DD_ENV` y `DD_VERSION` del mismo contenedor.

---

## Verificar que APM funciona

Una vez que el nuevo task esté corriendo, revisar los logs del contenedor de la app en CloudWatch. Un arranque exitoso del tracer se ve así:

```
[dd:info] DATADOG TRACER CONFIGURATION - {"agent_url":"http://127.0.0.1:8126", "service":"netapp", ...}
```

Esa línea confirma que el profiler del CLR cargó correctamente. Después de generar tráfico, el servicio aparece en **Datadog → APM → Services**.
