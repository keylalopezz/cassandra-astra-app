# Pulso Tacna · Series de tiempo con Apache Cassandra en Astra DB

[![ci-cd](https://github.com/keylalopezz/cassandra-astra-app/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/keylalopezz/cassandra-astra-app/actions/workflows/ci-cd.yml)
[![infra](https://github.com/keylalopezz/cassandra-astra-app/actions/workflows/infra.yml/badge.svg)](https://github.com/keylalopezz/cassandra-astra-app/actions/workflows/infra.yml)
[![ingesta](https://github.com/keylalopezz/cassandra-astra-app/actions/workflows/ingesta.yml/badge.svg)](https://github.com/keylalopezz/cassandra-astra-app/actions/workflows/ingesta.yml)

Aplicación de monitoreo de estaciones meteorológicas de Tacna construida sobre una base de datos **NoSQL orientada a columnas**: **Apache Cassandra**, administrada en **DataStax Astra DB** (serverless). Publicada en **Render** y desplegada de punta a punta con **Terraform + GitHub Actions**.

**App publicada:** https://pulso-tacna.onrender.com

| Recurso | Enlace |
|---|---|
| Artículo de Keyla — Modelado orientado a consultas en Apache Cassandra | https://dev.to/keylalopezz/modelado-orientado-a-consultas-en-apache-cassandra-series-de-tiempo-con-particiones-ttl-y-40hj |
| Artículo de Adrian — Cassandra como código (Terraform + GitHub Actions) | https://dev.to/adriansalasepis/cassandra-como-codigo-astra-db-render-con-terraform-y-un-pipeline-de-github-actions-que-prueba-7jg |
| Video (YouTube, 4:13) | https://youtu.be/6Cq9YjIz8pg |
| Automatización (GitHub Actions) | https://github.com/keylalopezz/cassandra-astra-app/actions |

> Actividad grupal — Base de Datos II, Escuela Profesional de Ingeniería de Sistemas, Universidad Privada de Tacna.
> Autores: **Adrian Fabricio Salas Acuña** y **Keyla Noelia Lopez Ojeda**.

## Qué hace

- 5 estaciones (de la costa a la sierra de Tacna) registran **temperatura y humedad**.
- Cada lectura se guarda en una partición **(estación, día)** ordenada por hora descendente, con **TTL de 30 días**.
- Un **counter** de Cassandra lleva el número de lecturas por estación y por día.
- El panel muestra las últimas lecturas, la serie de 24 horas y **la sentencia CQL** que ejecuta cada acción.

## Arquitectura

```
git push ──► GitHub Actions ─┬─ ci-cd.yml : pruebas con Cassandra 5.0 real ─► imagen ghcr.io ─► deploy Render ─► verifica versión
                             ├─ infra.yml : Terraform ─► Astra DB (keyspace, token, bundle) + Render Web Service
                             └─ ingesta.yml (cada hora): POST /api/simular ─► nuevas lecturas en Astra DB

Navegador ──► Render (ASP.NET Core 10) ──CQL + mTLS (Secure Connect Bundle)──► Astra DB (Cassandra, AWS us-east-2)
```

| Pieza | Tecnología |
|---|---|
| Base de datos | Apache Cassandra en DataStax Astra DB serverless (AWS us-east-2) |
| API y panel | ASP.NET Core 10 (minimal API) + DataStax C# Driver 3.23 + Chart.js |
| Hosting | Render Web Service (plan free) con la imagen de GitHub Packages |
| Infraestructura | Terraform: `datastax/astra`, `render-oss/render`, `hashicorp/http` |
| Automatización | GitHub Actions: `ci-cd.yml`, `infra.yml`, `ingesta.yml` |

## Modelo de datos (query-first)

```sql
CREATE TABLE lecturas_por_estacion_dia (
    estacion_id text,
    dia         date,
    ts          timestamp,
    temperatura double,
    humedad     double,
    PRIMARY KEY ((estacion_id, dia), ts)
) WITH CLUSTERING ORDER BY (ts DESC);

CREATE TABLE conteo_lecturas_dia (
    dia         date,
    estacion_id text,
    total       counter,
    PRIMARY KEY ((dia), estacion_id)
);
```

| Consulta de la app | Cómo la resuelve Cassandra |
|---|---|
| Última lectura de una estación | `WHERE estacion_id = ? AND dia = ? LIMIT 1` (una partición, la fila ya está primero) |
| Serie de las últimas 24 h | Partición de hoy + partición de ayer |
| Lecturas de hoy por estación | `conteo_lecturas_dia` (counter) |
| Borrar datos viejos | `USING TTL 2592000` (30 días) |

## API

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/health` | Estado de la conexión con Cassandra |
| GET | `/api/info` | Versión (commit), motor, datacenter y latencia |
| GET | `/api/estaciones` | Estaciones con su última lectura y el conteo del día |
| GET | `/api/estaciones/{id}/lecturas?horas=24` | Serie de tiempo de una estación |
| POST | `/api/lecturas` | Registra `{ estacionId, temperatura, humedad }` |
| POST | `/api/simular?rondas=5` | Genera lecturas realistas para todas las estaciones |

## Ejecutar en local

```bash
docker run -d --name cassandra -p 9042:9042 cassandra:5.0
CASSANDRA_CONTACT_POINTS=localhost dotnet run --project src/PulsoApi
CASSANDRA_CONTACT_POINTS=localhost dotnet test     # 13 pruebas, 5 de integración contra Cassandra
```

## Secretos de GitHub Actions

| Secreto | Uso |
|---|---|
| `ASTRA_TOKEN` | Token de Astra (Organization Administrator) para Terraform |
| `RENDER_API_KEY`, `RENDER_OWNER_ID` | Crear y desplegar el Web Service |
| `GHCR_TOKEN` | Token `read:packages` para que Render descargue la imagen |
| `TF_STATE_PASSPHRASE` | Cifra con AES-256 el estado de Terraform (el repositorio es público) |

La app recibe en Render un **token propio con rol Database Administrator** (creado por Terraform), nunca el token de organización.
