# Datadog APM — .NET Tracer on ECS Fargate

## Overview

The Datadog sidecar agent handles infrastructure metrics. APM (HTTP traces, HttpClient spans, etc.) requires a second component: the **.NET tracer**, which lives **inside your application image**.

The tracer installs as a **native CLR profiler** — it hooks into the .NET runtime before your code starts, with zero changes to business logic.

---

## Step-by-Step: Install the Tracer in the Dockerfile

### 1. Download the tarball

Go to: `https://github.com/DataDog/dd-trace-dotnet/releases`

Download: `datadog-dotnet-apm-X.X.X.tar.gz`
(generic Linux x64 tarball — not the `.deb` or `.rpm`)

Place it in the same directory as your `Dockerfile`.

### 2. Add to the `runtime` stage of your Dockerfile

```dockerfile
# Copy the tarball into the image
COPY datadog-dotnet-apm-3.42.0.tar.gz /tmp/dd-tracer.tar.gz

# Extract it — this step is the one most people forget
RUN mkdir -p /opt/datadog && \
    tar -xz -C /opt/datadog -f /tmp/dd-tracer.tar.gz && \
    rm /tmp/dd-tracer.tar.gz

# Tell the .NET CLR to activate the profiler
ENV CORECLR_ENABLE_PROFILING=1
ENV CORECLR_PROFILER={846F5F1C-F9AE-4B07-969E-05C26BC060D8}
ENV CORECLR_PROFILER_PATH=/opt/datadog/Datadog.Trace.ClrProfiler.Native.so
ENV DD_DOTNET_TRACER_HOME=/opt/datadog
```

> **Common mistake:** copying the tarball without the `RUN tar` step. The file arrives in the image but is never unpacked. The CLR looks for the `.so` at `/opt/datadog/`, finds nothing, and silently skips the profiler. The app runs normally but sends zero traces.

---

## What Each CLR Variable Does

| Variable | Purpose |
|---|---|
| `CORECLR_ENABLE_PROFILING=1` | Tells the .NET runtime to activate a profiler |
| `CORECLR_PROFILER={846F5F1C...}` | The GUID that identifies the Datadog profiler to the CLR |
| `CORECLR_PROFILER_PATH` | Path to the native `.so` injected into the process |
| `DD_DOTNET_TRACER_HOME` | Base directory where the tracer looks for its config and managed DLLs |

These four variables together enable auto-instrumentation: HttpClient calls, ASP.NET Core middleware, and more — without touching application code.

---

## Environment Variables per Container

### `net-app` (your application)

| Variable | Value | Purpose |
|---|---|---|
| `CORECLR_ENABLE_PROFILING` | `1` | Activates the CLR profiler |
| `CORECLR_PROFILER` | `{846F5F1C-F9AE-4B07-969E-05C26BC060D8}` | Datadog profiler GUID |
| `CORECLR_PROFILER_PATH` | `/opt/datadog/Datadog.Trace.ClrProfiler.Native.so` | Native profiler path |
| `DD_DOTNET_TRACER_HOME` | `/opt/datadog` | Tracer home directory |
| `DD_AGENT_HOST` | `127.0.0.1` | Agent address (same task = shared network in `awsvpc`) |
| `DD_TRACE_AGENT_PORT` | `8126` | Agent APM port |
| `DD_SERVICE` | `netapp` | Service name in Datadog |
| `DD_ENV` | `dev` | Environment tag |
| `DD_VERSION` | `1.0.0` | Version for Unified Service Tagging |

### `datadog-agent`

| Variable | Value | Purpose |
|---|---|---|
| `DD_API_KEY` | *(from Secrets Manager)* | Datadog authentication |
| `DD_SITE` | `datadoghq.com` | Ingestion endpoint |
| `DD_APM_ENABLED` | `true` | Enables the trace receiver in the agent |
| `DD_APM_NON_LOCAL_TRAFFIC` | `true` | Accepts traces from other containers in the task |
| `DD_ENV` | `dev` | Tags the environment on agent metrics |
| `ECS_FARGATE` | `true` | Tells the agent to use the Fargate metadata API instead of the Docker socket |

---

## Verify APM is Working

Once the new task is running, check the app container logs in CloudWatch. A successful tracer startup looks like:

```
[dd:info] DATADOG TRACER CONFIGURATION - {"agent_url":"http://127.0.0.1:8126", "service":"netapp", ...}
```

This line confirms the CLR profiler loaded. After generating traffic, the service appears under **Datadog → APM → Services**.
