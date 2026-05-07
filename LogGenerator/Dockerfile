FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY LogGenerator.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Download the Datadog .NET tracer ──────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ARG DD_TRACER_VERSION=3.42.0
COPY datadog-dotnet-apm-3.42.0.tar.gz /tmp/dd-tracer.tar.gz
RUN mkdir -p /opt/datadog && \
    tar -xz -C /opt/datadog -f /tmp/dd-tracer.tar.gz && \
    rm /tmp/dd-tracer.tar.gz

# CLR profiler environment variables (enable auto-instrumentation)
ENV CORECLR_ENABLE_PROFILING=1
ENV CORECLR_PROFILER={846F5F1C-F9AE-4B07-969E-05C26BC060D8}
ENV CORECLR_PROFILER_PATH=/opt/datadog/Datadog.Trace.ClrProfiler.Native.so
ENV DD_DOTNET_TRACER_HOME=/opt/datadog 


RUN groupadd -r appuser && useradd -r -g appuser appuser

COPY --from=build /app/publish .

RUN chown -R appuser:appuser /app
USER appuser

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=production

EXPOSE 8080

# HEALTHCHECK sin curl → usa el healthcheck de ASP.NET nativo
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -qO- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "LogGenerator.dll"]
