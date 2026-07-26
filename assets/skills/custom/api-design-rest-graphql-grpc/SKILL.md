---
name: api-design-rest-graphql-grpc
description: Architecting high-performance APIs: RESTful resource modeling, OpenAPI/Swagger specifications, GraphQL schemas, and gRPC Protocol Buffers.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# API Design: REST, GraphQL, and gRPC

Designing clean, scalable APIs requires choosing the right protocol (REST, GraphQL, or gRPC) and adhering to strict contract conventions.

## Protocol Comparison

| Dimension | REST | GraphQL | gRPC |
| :--- | :--- | :--- | :--- |
| **Transport** | HTTP/1.1 or HTTP/2 | HTTP/1.1 or HTTP/2 | HTTP/2 Multiplexed |
| **Payload Format** | JSON / XML | JSON | Binary Protobuf |
| **Schema Contract** | OpenAPI (Optional) | GraphQL Schema (Strict) | `.proto` File (Strict) |
| **Best For** | Public APIs, Web Apps | Aggregated UI feeds, Mobile | Internal Microservices |

---

## RESTful API Conventions

- **Nouns over Verbs**: `/api/v1/users` instead of `/api/v1/getUsers`.
- **HTTP Verbs**: `GET` (read), `POST` (create), `PUT` (replace), `PATCH` (partial edit), `DELETE` (remove).
- **Consistent Status Codes**: `200 OK`, `201 Created`, `400 Bad Request`, `401 Unauthorized`, `403 Forbidden`, `404 Not Found`, `500 Server Error`.

### OpenAPI Schema Fragment Blueprint
```yaml
paths:
  /users/{id}:
    get:
      summary: Retrieve user by ID
      parameters:
        - name: id
          in: path
          required: true
          schema:
            type: string
            format: uuid
      responses:
        '200':
          description: User object
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/User'
```

---

## gRPC Protocol Buffer Blueprint (`user_service.proto`)

```protobuf
syntax = "proto3";

package user.v1;

service UserService {
  rpc GetUser (GetUserRequest) returns (GetUserResponse);
}

message GetUserRequest {
  string id = 1;
}

message GetUserResponse {
  string id = 1;
  string email = 2;
  string name = 3;
}
```

---

## Verification Checklist

- [ ] API endpoints use plural nouns for collections (`/orders`, `/products`).
- [ ] Error responses follow RFC 7807 Problem Details JSON format.
- [ ] Breaking API changes introduce a new version path (`/v2/`).
- [ ] OpenAPI / GraphQL / Protobuf schemas are validated automatically in CI.
