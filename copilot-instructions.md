# AI Rules for OTELStdApi



## BACKEND


#### ENTITY_FRAMEWORK

- Use the repository and unit of work patterns to abstract data access logic and simplify testing
- Implement eager loading with Include() to avoid N+1 query problems for {{entity_relationships}}
- Use migrations for database schema changes and version control with proper naming conventions
- Apply appropriate tracking behavior (AsNoTracking() for read-only queries) to optimize performance
- Implement query optimization techniques like compiled queries for frequently executed database operations
- Use value conversions for complex property transformations and proper handling of {{custom_data_types}}



## DATABASE

### Guidelines for SQL

#### POSTGRES

- Use connection pooling to manage database connections efficiently
- Implement JSONB columns for semi-structured data instead of creating many tables for {{flexible_data}}



## CODING_PRACTICES

### Guidelines for DOCUMENTATION

#### DOC_UPDATES

- Update relevant documentation in /docs when modifying features
- Keep README.md in sync with new capabilities
- Maintain changelog entries in CHANGELOG.md




### Guidelines for ARCHITECTURE

#### ADR

- Create ADRs in /docs/adr/{name}.md for:
- 1) Major dependency changes
- 2) Architectural pattern changes
- 3) New integration patterns
- 4) Database schema changes


### Guidelines for Observability

- use logging, metrics and traces in OTEL standard

#### Traces (Distributed Tracing)
- Implementacja: System.Diagnostics.ActivitySource
- Eksport: OTLP (OpenTelemetry Protocol) 
- Użycie: Śledzenie przebiegu żądań, nested spans, custom tags
- W3C Trace Context: Automatyczne propagowanie traceparent, tracestate headers w outgoing HTTP requests
- Baggage Propagation: Propagowanie baggage items (request.id, deployment.environment) w całej aplikacji i do zewnętrznych API

#### metrics
- Implementacja: System.Diagnostics.Metrics.Meter i Counter, Histogram
- Eksport: OTLP (OpenTelemetry Protocol) 

#### logs
- mplementacja: ILogger<T> z OpenTelemetry
- Eksport: OTLP do Alloy
- Funkcje: Structured logging, scopes, kontekst żądania


