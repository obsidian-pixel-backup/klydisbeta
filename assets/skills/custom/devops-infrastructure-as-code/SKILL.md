---
name: devops-infrastructure-as-code
description: Best practices for Infrastructure as Code (IaC) using Terraform, OpenTofu, Ansible, and CloudFormation — declarative configuration, state file locking, module reusability, secret handling, drift detection, and automated deployment pipelines. Use when writing IaC templates, provisioning cloud infrastructure, or managing cloud environments.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Infrastructure as Code (IaC)

Infrastructure as Code manages cloud environments (compute, networking, storage, databases) using declarative version-controlled configuration files rather than manual console configuration.

## Core IaC Principles

1. **Declarative Configuration**: Define *what* the desired state should be; let the IaC engine calculate the execution plan.
2. **Immutability**: Replace instances rather than modifying running servers in-place.
3. **Idempotency**: Running `terraform apply` twice with identical configuration results in zero state changes on the second run.

## State Management & Safety

- **Remote State Locking**: Store Terraform state in a remote bucket (S3/Azure Blob) with strict state locking (DynamoDB/state lock) to prevent simultaneous apply operations.
- **Isolate Workspaces**: Separate state files per environment (`dev`, `staging`, `prod`) and cloud region.
- **Never Store Hardcoded Credentials**: Pass secrets via dynamic environment variables or secret store data sources (`aws_secretsmanager_secret_version`).

## Example Module Structure

```
modules/
  vpc/
    main.tf
    variables.tf
    outputs.tf
environments/
  dev/
    main.tf
    terragrunt.hcl
  prod/
    main.tf
    terragrunt.hcl
```

## Checklist

- [ ] Terraform state stored remotely with encryption and lock table enabled
- [ ] Environments (`dev`, `prod`) use completely separate state files
- [ ] `terraform plan` output reviewed in CI before applying changes
- [ ] Secret values marked as `sensitive = true` to prevent console output leaks
