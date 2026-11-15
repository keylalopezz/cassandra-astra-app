terraform {
  required_version = ">= 1.6"

  required_providers {
    astra = {
      source  = "datastax/astra"
      version = "~> 2.5"
    }
    render = {
      source  = "render-oss/render"
      version = "~> 1.9"
    }
    http = {
      source  = "hashicorp/http"
      version = "~> 3.6"
    }
  }
}

# Credenciales desde variables de entorno (secretos de GitHub Actions):
# ASTRA_API_TOKEN, RENDER_API_KEY y RENDER_OWNER_ID.
provider "astra" {}

provider "render" {
  wait_for_deploy_completion = true
}

# ---------------------------------------------------------------------------
# Variables
# ---------------------------------------------------------------------------
variable "astra_region" {
  description = "Región de AWS para la base serverless de Astra DB"
  type        = string
  default     = "us-east-2"
}

variable "keyspace" {
  description = "Keyspace de Cassandra de la aplicación"
  type        = string
  default     = "pulso"
}

variable "image_url" {
  description = "Imagen de la app publicada en GitHub Packages (ghcr.io)"
  type        = string
}

variable "image_tag" {
  description = "Etiqueta de la imagen"
  type        = string
  default     = "latest"
}

variable "ghcr_username" {
  type = string
}

variable "ghcr_token" {
  type      = string
  sensitive = true
}

# ---------------------------------------------------------------------------
# Base de datos: Apache Cassandra administrado en DataStax Astra DB (serverless)
# ---------------------------------------------------------------------------
resource "astra_database" "pulso" {
  name           = "pulso-tacna"
  keyspace       = var.keyspace
  cloud_provider = "aws"
  regions        = [var.astra_region]
  db_type        = "vector"

  timeouts {
    create = "30m"
  }
}

# Token de aplicación con permisos solo sobre bases de datos (no de organización).
data "astra_roles" "roles" {}

locals {
  rol_db_admin = one([for r in data.astra_roles.roles.results : r.role_id if r.role_name == "Database Administrator"])
}

resource "astra_token" "app" {
  roles = [local.rol_db_admin]
}

# Secure Connect Bundle: host, puerto y certificados mTLS para el driver de Cassandra.
data "astra_secure_connect_bundle_url" "scb" {
  database_id = astra_database.pulso.id
}

data "http" "scb" {
  url = data.astra_secure_connect_bundle_url.scb.secure_bundles[0].url
}

# ---------------------------------------------------------------------------
# Hosting: Web Service de contenedores en Render
# ---------------------------------------------------------------------------
resource "render_registry_credential" "ghcr" {
  name       = "ghcr-pulso-tacna"
  registry   = "GITHUB"
  username   = var.ghcr_username
  auth_token = var.ghcr_token
}

resource "render_web_service" "app" {
  name              = "pulso-tacna"
  plan              = "free"
  region            = "oregon"
  health_check_path = "/health"

  runtime_source = {
    image = {
      image_url              = var.image_url
      tag                    = var.image_tag
      registry_credential_id = render_registry_credential.ghcr.id
    }
  }

  env_vars = {
    "PORT"                = { value = "8080" }
    "ASTRA_KEYSPACE"      = { value = var.keyspace }
    "ASTRA_CLIENT_ID"     = { value = astra_token.app.client_id }
    "ASTRA_CLIENT_SECRET" = { value = astra_token.app.secret }
    "ASTRA_SCB_B64"       = { value = data.http.scb.response_body_base64 }
  }

  lifecycle {
    # La URL del bundle cambia en cada lectura, pero su contenido es el mismo:
    # no se redespliega por eso. deployimage cambia la imagen, no Terraform.
    ignore_changes = [env_vars["ASTRA_SCB_B64"], runtime_source]
  }
}

# ---------------------------------------------------------------------------
# Salidas
# ---------------------------------------------------------------------------
output "app_url" {
  value = render_web_service.app.url
}

output "render_service_id" {
  value = render_web_service.app.id
}

output "astra_database_id" {
  value = astra_database.pulso.id
}

output "astra_region" {
  value = var.astra_region
}

output "astra_status" {
  value = astra_database.pulso.status
}
