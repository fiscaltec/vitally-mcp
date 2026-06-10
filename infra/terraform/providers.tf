terraform {
  required_version = ">= 1.6"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }

  # Remote state. Create a storage account + "tfstate" container first (see README),
  # then uncomment and `terraform init -migrate-state`.
  # backend "azurerm" {
  #   resource_group_name  = "vitally-prod-rg-uksouth"
  #   storage_account_name = "<tfstate-storage-account>"
  #   container_name       = "tfstate"
  #   key                  = "vitally-mcp.tfstate"
  #   use_oidc             = true
  # }
}

provider "azurerm" {
  subscription_id = var.subscription_id
  features {
    key_vault {
      # Never auto-purge: both vaults have purge protection on.
      purge_soft_delete_on_destroy = false
    }
  }
}
