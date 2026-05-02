 Datadog APM para .NET en ECS Fargate

  ---
  Por qué descargamos el tracer manualmente

  El agente de Datadog (el sidecar) recolecta métricas e infraestructura. Pero para APM — trazas de tus requests HTTP, spans
  de HttpClient, etc. — necesitas un segundo componente: el tracer de .NET, que vive dentro de tu imagen de aplicación.

  El tracer se instala como un profiler nativo del CLR. Esto significa que se engancha al runtime de .NET a nivel bajo, antes
  de que tu código arranque, sin que tengas que modificar nada de tu lógica de negocio.

  ---
  Paso a paso: descargar e instalar el tracer en el Dockerfile

  1. Descarga el tarball desde GitHub Releases

  Entra a: https://github.com/DataDog/dd-trace-dotnet/releases

  Descarga el archivo: datadog-dotnet-apm-X.X.X.tar.gz
  (el tar.gz genérico para Linux x64, no el .deb ni el .rpm)

  Cópialo al directorio donde está tu Dockerfile.

  2. Agrega estas líneas en el stage runtime de tu Dockerfile

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

  El error común: copiar el tarball y olvidar el RUN tar. El archivo llega al contenedor pero nunca se desempaca. El CLR busca
   el .so en /opt/datadog/, no lo encuentra, y silenciosamente ignora el profiler. La app corre normal pero sin APM.

  ---
  Qué hace cada variable CLR

  ┌────────────────────────────────┬────────────────────────────────────────────────────────────────────────────────────┐
  │            Variable            │                                   Para qué sirve                                   │
  ├────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
  │ CORECLR_ENABLE_PROFILING=1     │ Le dice al runtime de .NET: "hay un profiler, actívalo"                            │
  ├────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
  │ CORECLR_PROFILER={846F5F1C...} │ El GUID que identifica el profiler de Datadog ante el CLR                          │
  ├────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
  │ CORECLR_PROFILER_PATH          │ Ruta al .so nativo que se inyecta en el proceso                                    │
  ├────────────────────────────────┼────────────────────────────────────────────────────────────────────────────────────┤
  │ DD_DOTNET_TRACER_HOME          │ Directorio base donde el tracer busca sus archivos de configuración y managed dlls │
  └────────────────────────────────┴────────────────────────────────────────────────────────────────────────────────────┘

  Estas cuatro variables juntas hacen que el tracer se auto-instrumente: intercepta HttpClient, ASP.NET Core middleware, y más
   — sin tocar código.

  ---
  Variables de entorno por contenedor

  Variables net-app (dentro de tu aplicación, docker file)

  ┌──────────────────────────┬──────────────────────────────────────────────────┬──────────────────────────────────────────┐
  │         Variable         │                      Valor                       │                 Por qué                  │
  ├──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┤
  │ CORECLR_ENABLE_PROFILING │ 1                                                │ Activa el profiler en el CLR             │
  ├──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┤
  │ CORECLR_PROFILER         │ {846F5F1C-F9AE-4B07-969E-05C26BC060D8}           │ GUID del profiler de Datadog             │
  ├──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┤
  │ CORECLR_PROFILER_PATH    │ /opt/datadog/Datadog.Trace.ClrProfiler.Native.so │ Ruta al profiler nativo                  │
  ├──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┤
  └──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┘

  Variables de entorno de ECS Fargate de tu app container
  ┌──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┐
  │ DD_DOTNET_TRACER_HOME    │ /opt/datadog                                     │ Home del tracer                          │
  ├──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┤
  │ DD_AGENT_HOST            │ 127.0.0.1                                        │ Dirección del agente (mismo task = misma │
  │                          │                                                  │  red en awsvpc)                          │
  ├──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┤
  │ DD_TRACE_AGENT_PORT      │ 8126                                             │ Puerto APM del agente                    │
  ├──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┤
  │ DD_SERVICE               │ netapp                                           │ Nombre del servicio en Datadog           │
  ├──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┤
  │ DD_ENV                   │ dev                                              │ Ambiente                                 │
  ├──────────────────────────┼──────────────────────────────────────────────────┼──────────────────────────────────────────┤
  │ DD_VERSION               │ 1.0.0                                            │ Versión para Unified Service Tagging     │
  └──────────────────────────┴──────────────────────────────────────────────────┴──────────────────────────────────────────┘

  Contenedor: datadog-agent

  ┌──────────────────────────┬───────────────────────┬────────────────────────────────────────────────────────────────────┐
  │         Variable         │         Valor         │                              Por qué                               │
  ├──────────────────────────┼───────────────────────┼────────────────────────────────────────────────────────────────────┤
  │ DD_API_KEY               │ (desde Secrets        │ Autenticación con Datadog                                          │
  │                          │ Manager)              │                                                                    │
  ├──────────────────────────┼───────────────────────┼────────────────────────────────────────────────────────────────────┤
  │ DD_SITE                  │ datadoghq.com         │ Endpoint de ingestión                                              │
  ├──────────────────────────┼───────────────────────┼────────────────────────────────────────────────────────────────────┤
  │ DD_APM_ENABLED           │ true                  │ Activa el receptor de trazas en el agente                          │
  ├──────────────────────────┼───────────────────────┼────────────────────────────────────────────────────────────────────┤
  │ DD_APM_NON_LOCAL_TRAFFIC │ true                  │ Acepta trazas desde otros contenedores del task                    │
  ├──────────────────────────┼───────────────────────┼────────────────────────────────────────────────────────────────────┤
  │ DD_ENV                   │ dev                   │ Etiqueta el ambiente en las métricas del agente                    │
  ├──────────────────────────┼───────────────────────┼────────────────────────────────────────────────────────────────────┤
  │ ECS_FARGATE              │ true                  │ Le dice al agente que use la metadata API de Fargate en vez del    │
  │                          │                       │ Docker socket                                                      │
  └──────────────────────────┴───────────────────────┴────────────────────────────────────────────────────────────────────┘
