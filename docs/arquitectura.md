# Arquitectura

Gateway YARP → API → IReconstructionFacade → casos de uso → entidades y políticas.

![arquitectura.png](arquitectura.png)


- Domain: proyectos, actividades, reportes, propuestas y política de reparto. No referencia EF, HTTP o RabbitMQ.
- Application: puertos, comandos, consultas, orquestación y eventos tipados.
- Platform.DAL: EF Core, conversiones JSON de colecciones, migraciones, repositorios, unidad de trabajo, auditoría, Inbox y Outbox.
- Infrastructure: HTTP de desastres/daños/donaciones, almacenamiento de adjuntos y RabbitMQ.
- Platform.Security: autenticación JWT y adaptación de claims a permisos por desastre.
- API: contrato HTTP y composición; Gateway: proxy y presentación HTML/JavaScript.

