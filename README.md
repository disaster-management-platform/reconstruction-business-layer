
# Disaster Management Platform — Reconstrucción

Implementación del módulo de Reconstrucción de la plataforma de gestión de desastres. Permite generar propuestas a partir de evaluaciones de daños, formalizar proyectos, gestionar sus presupuestos y actividades, registrar avances y consolidar los resultados de ejecución.

Desarrollado en .NET 10, sobre el backend de desastres existente, conservando la compatibilidad con el cliente Android.

## Arquitectura

![Arquitectura](docs/arquitectura.png)

El sistema sigue una arquitectura orientada a servicios con arquitectura hexagonal. Las reglas de negocio se mantienen independientes de los mecanismos de persistencia, comunicación e integración.

- **Domain:** entidades, estados, políticas y reglas de negocio.
- **Application:** servicios de negocio, casos de uso e interfaces de comunicación.
- **Infrastructure:** adaptadores HTTP, mensajería y almacenamiento de archivos.
- **DAL:** `DisasterPlatform.Core 4.0.0` y `DisasterPlatform.DAL 6.0.0`, con Entity Framework Core y PostgreSQL. Los esquemas `disasters` y `reconstruction` mantienen sus propios repositorios y contextos.
- **API Gateway:** implementado con YARP para centralizar el acceso a los módulos y servir la interfaz web.

Las nuevas propuestas y proyectos requieren que el desastre esté en Recovery o Reconstruction. Closed permite consultar y finalizar proyectos históricos, sin generar proyectos nuevos ni reclasificar automáticamente el desastre. Las fases se consultan mediante HTTP al servicio propietario.

El patrón Facade proporciona un punto de acceso a los servicios de Reconstrucción. La colaboración asíncrona se realiza mediante RabbitMQ, utilizando eventos tipados y colas independientes por consumidor.

Se emplean los patrones Transactional Outbox e Inbox para preservar la consistencia entre la persistencia y la publicación de eventos, controlar duplicados y permitir la recuperación ante fallos de comunicación.

### Servicios de negocio

| Servicio | Responsabilidad |
|---|---|
| Definición | Clasificación de daños, generación de propuestas y distribución presupuestal según la gravedad promedio. |
| Formalización | Creación de proyectos, asignación de responsables, reserva presupuestal y definición del cronograma. |
| Monitoreo | Seguimiento de actividades, registro de costos, evidencias y revisiones presupuestales. |
| Finalización | Validación de actividades terminadas, consolidación de reportes y liberación del presupuesto no ejecutado. |

Los cambios de estado y presupuesto se registran mediante mecanismos de auditoría y control de concurrencia.


## Lógica de negocio

El proceso de reconstrucción se divide en cuatro etapas, cada una implementada mediante un servicio de negocio con responsabilidades independientes.

### 1. Definición de propuestas

Se consultan las evaluaciones de daños validadas y se agrupan por zona geográfica y tipo de activo. A cada nivel de daño se le asigna un peso que permite calcular la gravedad promedio de cada grupo y distribuir proporcionalmente el presupuesto disponible de donaciones.

Las propuestas se actualizan cuando cambian las evaluaciones. El usuario selecciona cuáles se convertirán en proyectos de reconstrucción.

### 2. Formalización

Para cada propuesta seleccionada se define un proyecto con presupuesto, fuente de financiación, responsable, contratista y cronograma de actividades.

El presupuesto se reserva de manera transaccional, evitando comprometer recursos superiores a los disponibles. Una vez confirmada la formalización, el proyecto pasa a monitoreo.

### 3. Monitoreo

Se realiza el seguimiento de las actividades mediante reportes que registran su estado, costos ejecutados, evidencias y documentación de soporte.

El servicio controla el presupuesto de cada actividad y permite realizar revisiones presupuestales justificadas sin perder el historial de ejecución.

### 4. Finalización y cierre

Un proyecto puede cerrarse cuando todas sus actividades se encuentran terminadas y cuentan con sus respectivos reportes y evidencias.

Se genera un reporte consolidado de la ejecución y se libera el presupuesto no utilizado, conservando el historial del proyecto y los costos ejecutados.

### Reglas transversales

- Control de las transiciones de estado de propuestas, proyectos y actividades.
- Auditoría de operaciones y modificaciones presupuestales.
- Control de concurrencia para evitar actualizaciones inconsistentes.
- Idempotencia en el registro de reportes para evitar duplicidad de costos.
- Comunicación asíncrona entre servicios mediante eventos de negocio publicados en RabbitMQ.

## Tecnologías

| Componente | Tecnología |
|---|---|
| Backend | .NET 10 / ASP.NET Core |
| Persistencia | Entity Framework Core / PostgreSQL |
| API Gateway | YARP |
| MOM | RabbitMQ / AMQP |
| Integración | REST / JSON |
| Seguridad | JWT / OpenID Connect |
| Frontend | HTML, CSS, JavaScript y Leaflet |
| Pruebas | xUnit y Python |

## Ejecución

Se requiere .NET 10 y autenticación NuGet para GitHub Packages. Configura la conexión desde un archivo local protegido fuera de Git. El archivo de Reconstrucción debe contener una cadena Npgsql con su usuario propio, `SSL Mode=VerifyFull` y la CA confiable para conexiones TCP:

```bash
export RECONSTRUCTION_CONNECTION_STRING="$(cat "$RECONSTRUCTION_CONNECTION_FILE")"
export ExternalApis__Disasters=http://localhost:5156/
```

`run_local.py` inicia únicamente Reconstrucción y el Gateway; transmite `ConnectionStrings__Disasters` solo a Reconstrucción. Gestión de Desastres se inicia desde `disaster-backend-offline` y se consulta por `ExternalApis__Disasters`. El Gateway no recibe credenciales PostgreSQL. Su configuración base ofrece ambos módulos y un único frontend, sin el perfil funcional `Reconstruction`.

Compilar la solución y el Gateway:

```bash
dotnet restore DisasterLocation.sln
dotnet build DisasterLocation.sln
dotnet build ../disaster-api-gateway/DisasterLocation.Gateway.sln
```

Interfaz web: http://localhost:5080

Las migraciones se aplican explícitamente antes de ejecutar los servicios, con un archivo administrativo separado en `MIGRATION_CONNECTION_FILE`:

```bash
ConnectionStrings__Disasters="$(cat "$MIGRATION_CONNECTION_FILE")" \
  dotnet run --project ../disaster-backend-offline/DisasterLocation --no-launch-profile -- --Database:Migrate=true
```

Iniciar los procesos con sus credenciales de ejecución:

```bash
python3 scripts/run_local.py --demo
```

El arranque normal no aplica migraciones. No utilices la conexión administrativa para ejecutar la API. Gestión de Desastres es el único escritor de `disasters`; Reconstrucción solo escribe en `reconstruction` y conserva su integración HTTP `IDisasterReader`/`DisasterHttpReader`. Las políticas JWT y los permisos por desastre proceden de `DisasterPlatform.Security 1.0.0`.

Para habilitar la comunicación mediante RabbitMQ:

```bash
docker compose up -d rabbitmq
python3 scripts/run_local.py --demo --messaging
```

El modo de demostración utiliza una identidad local y datos simulados de evaluación de daños y donaciones. Fuera de este modo, se requiere configurar los servicios externos y el proveedor de identidad.

Endpoints de diagnóstico:

- `/api/reconstruccion/health`
- `/api/reconstruccion/ready`

## Integración

La comunicación con Desastres, Evaluación de Daños y Donaciones se realiza mediante adaptadores REST. Los contratos externos se mantienen separados del dominio, evitando dependencias directas entre los modelos de negocio de cada módulo.

Los adaptadores de Evaluación de Daños y Donaciones se validan actualmente mediante servicios HTTP de prueba, mientras se completa la integración con los módulos correspondientes.

El backend conserva la compatibilidad con el cliente Android, incluyendo el registro y la sincronización de desastres en modo desconectado.

La dirección del servidor móvil puede configurarse mediante:

```bash
-PAPI_BASE_URL=http://IP_DEL_SERVIDOR:5080/api/
```

Ver [contratos de integración](docs/integracion.md) y [documentación de arquitectura](docs/arquitectura.md).

## Seguridad

Los servicios de Reconstrucción utilizan autenticación JWT y autorización mediante roles y acceso por desastre. El proveedor de identidad se configura mediante las variables:

- `Security__Authority`
- `Security__Audience`

Las evidencias y documentos se almacenan fuera del directorio público, asociados a su proyecto y con verificación de integridad mediante SHA-256.

## Pruebas

Se requieren `psycopg2` y un PostgreSQL local por socket Unix con permiso para crear bases. Con `PGSOCKET` apuntando a ese directorio:

```bash
export TEST_POSTGRES_DSN="host=${PGSOCKET:?Configura el socket PostgreSQL local} port=5432 dbname=postgres user=$(id -un)"
export TEST_POSTGRES_CONNECTION="Host=$PGSOCKET;Port=5432;Database=postgres;Username=$(id -un)"
dotnet test DisasterLocation.sln
python3 scripts/platform_e2e.py
python3 scripts/messaging_e2e.py
python3 scripts/smoke_test.py
python3 scripts/monitoring_e2e.py
python3 scripts/finalization_e2e.py
python3 -m unittest discover -s scripts -p 'test_*.py'
```

Las pruebas cubren reglas de negocio, persistencia, concurrencia, seguridad, integración HTTP y comunicación asíncrona. Los scripts crean bases PostgreSQL y puertos temporales, aplican las migraciones antes del arranque y eliminan únicamente sus recursos. Las pruebas destructivas rechazan conexiones remotas. La suite completa del Gateway se ejecuta con `python3 ../disaster-api-gateway/scripts/test_gateway.py`. Las cuatro pruebas de `test_consolidate_databases.py` verifican actualmente inicialización e integridad PostgreSQL; la herramienta histórica de transferencia SQLite fue retirada. `messaging_e2e.py` requiere el broker disponible y crea un virtual host de prueba propio.

## Trabajo futuro

- Integración con los módulos de Evaluación de Daños, Donaciones e Identidad.
- Ampliación funcional de Gestión de Desastres según el enunciado, conservando el propietario único.
- Almacenamiento compartido de evidencias y documentos.
- Redundancia de componentes y validación de los escenarios de alta disponibilidad.
- Pruebas de carga para los escenarios de escalabilidad establecidos en los requisitos del sistema.

## Keycloak service credentials

Inbound JWT uses `Security__Authority` (HTTPS issuer), `Security__Audience=reconstruction-api` and existing subject/organization/disaster/role policies. For `ExternalApis__Disasters__OAuth`, `ExternalApis__DamageAssessments__OAuth` (and a supported Funding provider), configure `TokenEndpoint`, `ClientId`, `ClientSecret` externally. Tokens are cached and acquired before expiry; a 401 invalidates the token for the next operation without replaying mutations. The token endpoint requires HTTPS, has a three-second timeout, a 64 KiB response limit and no redirects. Provision separate least-privilege clients and audience grants. Existing bearer configuration remains for providers not migrated to OAuth; OAuth replaces it on outgoing requests when configured. Financial rules/providers are unchanged.

Run `dotnet test Reconstruction.Tests --filter FullyQualifiedName~ClientCredentialsTests`. The real-issuer check runs only with `SECURITY_OAUTH_ENDPOINT`, `SECURITY_OAUTH_CLIENT`, `SECURITY_OAUTH_SECRET` and trusted CA configuration. Its isolated test client must have a five-second token lifespan. It verifies caching and renewal, not domain eligibility.

### Container image

Build from the repository root, using BuildKit:

```sh
docker build --secret id=nuget_credentials,src="$NUGET_CREDENTIALS_FILE" -t reconstruction-business-layer:local .
```
The private file contains the NuGet credential value `Username=<account>;Password=<token>` for GitHubPackages. Use a read-only package token, permissions 0600; never pass it as an ARG, copy it into a layer, or use a runtime secret for restore.

The runtime runs as the image's non-root application UID, listens on 8080, and receives configuration and trust roots externally. `SDK_IMAGE` and `RUNTIME_IMAGE` can be set to approved image digests for repeatable releases. Version tags are pinned; use approved digests for immutable releases. Configure HTTPS at the trusted ingress and preserve HTTPS issuer/downstream validation; private CA roots must be installed through the deployment trust configuration, never bypassed. Publish ports only as required. No build or normal startup applies database migrations. Supply runtime credentials using the deployment secret store.

### Shared attachments (09A)

Production uses private Supabase Storage, through the existing `IAttachmentStore` port; no local fallback. Replicas share a dedicated bucket `reconstruction-attachments`. Create it **private**, with a maximum object size of at least 20 MiB + 8,196 bytes, allowing `application/octet-stream`. The objects contain a four-byte big-endian metadata length, JSON attachment metadata and the original bytes. A single immutable object preserves metadata and content together. No presigned or public URL is returned; the API validates project access and the original SHA-256 on downloads and report verification.

Configure externally:

- `Attachments__Storage__BaseUrl`: HTTPS Supabase project origin.
- `Attachments__Storage__Bucket`: `reconstruction-attachments`.
- `Attachments__Storage__PublishableKey`: publishable key (not a service-role secret).
- `Attachments__Storage__Email` / `Attachments__Storage__Password`: separate technical Auth identity shared by reconstruction replicas, not a business user's identity.

Review existing Storage policies for unintended broader grants. Apply `server/storage.sql` only after explicitly authorizing the new bucket and identity, with `psql -v storage_identity=<verified UUID> -f server/storage.sql`. No changes to the remote project are performed by the service. The runtime identity must have insert/select on this bucket only; it must not have update, delete or administrative access. Token acquisition is bounded and cached; unavailable storage fails attachment operations without writing to local disk. Standard uploads are capped at 20 MiB; connection/body deadlines and four concurrent operations per replica limit resource consumption. Validate large uploads against the selected Storage tier and network.

For isolated development only, `Attachments__LocalDevelopment=true` enables the existing file provider; it is never a production fallback. Before switching an existing deployment, stop attachment writers, back up the existing directory and run `python3 scripts/migrate_attachments.py <legacy_directory>` to check the complete set. Set the external Storage credentials, then pass `--apply` to upload and read-verify each immutable object without changing its ID. Re-running reconciles already copied data; no source file is removed. Resume writers only after all records are verified and replicas use the same private bucket. Keep the backup until operational acceptance. Storage provisioning, runtime build and cross-replica validation must be performed in an isolated environment before rollout.
