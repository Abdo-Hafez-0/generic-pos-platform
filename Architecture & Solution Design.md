# Generic Offline-First Inventory & POS Platform

# Architecture & Solution Design

**Project:** Generic Offline-First Inventory & POS Platform
**Technology:** C# / .NET
**Desktop Platform:** Windows
**Local Database:** SQLite
**Cloud Backend:** ASP.NET Core
**Architecture Style:** Modular Monolith + Offline-First + Extensible
**Document Type:** Technical Architecture Specification
**Status:** Architecture Baseline

---

# 1. Purpose

This document defines the technical architecture of the Generic Offline-First Inventory & POS Platform.

It establishes:

* Solution structure
* Project boundaries
* Module boundaries
* Dependency rules
* Layering rules
* Interface ownership
* Database ownership
* Module communication
* Application startup
* Licensing architecture
* License verification flow
* Update architecture
* Module installation architecture
* Offline behavior
* Cloud communication
* Security boundaries
* Data ownership
* Future extensibility

The purpose is to establish the architecture **before implementing business functionality**.

No business feature should be implemented in a way that violates these boundaries.

---

# 2. Architectural Goals

The architecture must satisfy the following goals.

## 2.1 Offline First

The client must continue operating without Internet connectivity.

The local SQLite database is the operational source of truth for the desktop application.

The Internet is an enhancement rather than a mandatory dependency.

---

# 2.2 Modular

Features must be separated into independent modules.

The system must avoid becoming a single tightly coupled application.

A module should be possible to:

* Develop independently
* Test independently
* Version independently
* License independently
* Update independently
* Disable independently
* Remove without breaking unrelated functionality

---

# 2.3 Generic

The architecture must not assume a particular industry.

The same platform should support:

* Retail
* Wholesale
* Warehouses
* Clothing
* Electronics
* Pharmacy
* Food
* Manufacturing
* Restaurants
* Other inventory-based businesses

Industry-specific functionality should be implemented as modules rather than embedded into the generic core.

---

# 2.4 Extensible

A new feature should be introduced by adding a module rather than modifying large portions of the existing platform.

The architecture should support future modules without requiring a rewrite of the core.

---

# 2.5 Secure

The architecture must protect:

* Customer data
* Licenses
* Module packages
* Application updates
* Cloud communication
* Authentication credentials
* Backup data

Modules and updates must be cryptographically verified before installation.

---

# 2.6 Commercially Flexible

The same application platform should support different customers with different feature sets.

A customer's license determines which capabilities are available.

Example:

```text
Customer A

POS              ✓
Inventory        ✓
Purchasing       ✓
Reports          ✓
Accounting       ✗
Cloud Backup     ✗
Multi-Branch     ✗
```

Another customer may have:

```text
Customer B

POS              ✓
Inventory        ✓
Purchasing       ✓
Reports          ✓
Accounting       ✓
Cloud Backup     ✓
Multi-Branch     ✓
```

---

# 3. High-Level Architecture

The system is divided into two major environments.

```text
                    ┌───────────────────────────┐
                    │       CLOUD PLATFORM      │
                    │                           │
                    │ Licensing                 │
                    │ Updates                   │
                    │ Customer Management       │
                    │ Module Registry            │
                    │ Cloud Backup               │
                    │ Administration             │
                    └─────────────┬─────────────┘
                                  │
                              HTTPS/API
                                  │
                    ┌─────────────▼─────────────┐
                    │       DESKTOP CLIENT      │
                    │                           │
                    │ Application Host           │
                    │ Module Host               │
                    │ Business Modules          │
                    │ Local Services             │
                    │ Licensing Client           │
                    │ Update Client              │
                    │                           │
                    │          SQLite            │
                    └───────────────────────────┘
```

The desktop client must remain operational if the cloud platform is unavailable.

---

# 4. Architectural Style

The client will use a:

> **Modular Monolith**

rather than microservices.

The application is one desktop process/application, but internally it consists of strongly isolated modules.

Conceptually:

```text
Desktop Application
│
├── Platform
│
├── Catalog Module
├── Inventory Module
├── Sales Module
├── POS Module
├── Purchasing Module
├── Customer Module
│
└── Optional Modules
    ├── Accounting
    ├── Loyalty
    ├── Manufacturing
    ├── Cloud Backup
    └── Multi-Branch
```

This gives us modularity without introducing distributed-system complexity into the local POS.

---

# 5. Solution Structure

The solution should eventually resemble:

```text
GenericPOS.sln

src/
│
├── Platform/
│
├── Client/
│
├── Modules/
│
├── Cloud/
│
└── Tools/
```

A more detailed structure follows.

---

# 6. Platform Projects

```text
src/
└── Platform/
```

The Platform contains functionality shared by modules.

---

## 6.1 Platform.Core

```text
Platform.Core
```

Responsibility:

* Fundamental abstractions
* Domain primitives
* Results
* Errors
* IDs
* Domain events
* Module abstractions
* Feature identifiers
* Basic contracts

This project must be extremely stable.

It should contain almost no business-specific logic.

### Platform.Core may contain:

```text
Entity
AggregateRoot
ValueObject
Result
Error
DomainEvent
ModuleId
FeatureId
EntityId
```

It may also define fundamental interfaces such as:

```text
IModule
IModuleManifest
IFeature
```

---

# 7. Platform.Application

```text
Platform.Application
```

Responsibility:

* Application-level abstractions
* Commands
* Queries
* Application services
* Transaction abstractions
* Current user abstraction
* Authorization abstractions
* Module access
* Feature access

It should coordinate application operations but should not contain UI code.

---

# 8. Platform.Infrastructure

```text
Platform.Infrastructure
```

Responsibility:

* EF Core
* SQLite
* File system
* Logging implementation
* Local configuration
* Transaction implementation
* Hardware infrastructure
* Network infrastructure
* Cryptography implementation
* Local storage

This project contains implementation details.

---

# 9. Platform.Contracts

```text
Platform.Contracts
```

This project contains stable contracts that modules can use to communicate without referencing each other's implementations.

Examples:

```text
IProductReader
IInventoryReader
ISalesReader
ICustomerReader
IPriceCalculator
```

This project must be carefully controlled.

It exists to prevent modules from directly referencing implementation projects.

---

# 10. Client Projects

```text
src/
└── Client/
```

---

## 10.1 Client.Desktop

This is the main Windows application.

Potential technology:

```text
WPF
```

Responsibilities:

* Application window
* Navigation
* UI composition
* User interaction
* Module UI hosting
* Startup
* Shutdown
* Error presentation

The UI should not contain business rules.

---

# 11. Client.Host

```text
Client.Host
```

Responsibility:

* Application bootstrapping
* Dependency injection
* Module discovery
* Module loading
* Configuration
* License initialization
* Update initialization
* Application lifecycle

Startup sequence:

```text
Application starts
       ↓
Load configuration
       ↓
Initialize infrastructure
       ↓
Initialize local database
       ↓
Load license
       ↓
Verify license locally
       ↓
Discover installed modules
       ↓
Determine enabled modules
       ↓
Register modules
       ↓
Start UI
```

---

# 12. Client.Licensing

```text
Client.Licensing
```

Responsibility:

* Local license storage
* License verification
* License lease management
* Entitlement lookup
* Activation
* Renewal
* License state

It communicates with the cloud licensing API through an abstraction.

The business modules should not communicate directly with the licensing server.

---

# 13. Client.Updater

```text
Client.Updater
```

Responsibility:

* Update discovery
* Package download
* Package verification
* Version validation
* Module installation
* Core application update
* Rollback
* Update lifecycle

The updater is independent from business modules.

---

# 14. Client.ModuleHost

```text
Client.ModuleHost
```

This is responsible for managing modules.

Responsibilities:

* Discover modules
* Read module manifests
* Validate module packages
* Resolve module dependencies
* Check entitlements
* Load modules
* Register module services
* Register module UI
* Activate/deactivate modules

---

# 15. Module Structure

Each business module should follow a consistent internal structure.

Example:

```text
Modules/
└── Inventory/
    │
    ├── Inventory.Domain
    ├── Inventory.Application
    ├── Inventory.Contracts
    ├── Inventory.Infrastructure
    └── Inventory.UI
```

Not every module necessarily needs every project, but the structure should be standardized.

---

# 16. Module.Domain

Example:

```text
Inventory.Domain
```

Contains:

* Entities
* Value objects
* Domain rules
* Aggregates
* Domain events
* Business invariants

It must not depend on:

* WPF
* EF Core
* ASP.NET
* SQLite
* Cloud APIs

---

# 17. Module.Application

Example:

```text
Inventory.Application
```

Contains:

* Commands
* Queries
* Use cases
* Application services
* Validators
* DTOs
* Application orchestration

Example:

```text
CreateStockAdjustment
TransferStock
GetCurrentStock
GetStockHistory
```

---

# 18. Module.Contracts

Example:

```text
Inventory.Contracts
```

Contains only the public contracts other modules are allowed to consume.

For example:

```text
IInventoryReader
IStockAvailabilityChecker
IStockMovementReader
```

The goal is:

```text
Sales
   ↓
Inventory.Contracts
```

instead of:

```text
Sales
   ↓
Inventory.Infrastructure
```

This keeps module implementations private.

---

# 19. Module.Infrastructure

Example:

```text
Inventory.Infrastructure
```

Contains:

* EF Core configuration
* SQLite mappings
* Repositories
* Queries
* Database access
* External infrastructure

Only the module's infrastructure should know how its persistence works.

---

# 20. Module.UI

Example:

```text
Inventory.UI
```

Contains:

* Views
* ViewModels
* Navigation entries
* UI-specific services
* UI resources

The UI communicates with the Application layer.

It does not directly manipulate EF Core or SQLite.

---

# 21. Initial Module Set

The initial architecture should define the following modules.

```text
Modules/
│
├── Catalog
├── Inventory
├── Sales
├── POS
├── Purchasing
├── Customers
├── Suppliers
├── Pricing
├── Payments
├── Reporting
├── Users
├── Audit
└── CashManagement
```

Some of these may eventually be merged or divided after implementation experience.

The important point is to define responsibilities clearly.

---

# 22. Optional Module Set

Future modules:

```text
OptionalModules/
│
├── Accounting
├── Loyalty
├── AdvancedReports
├── AdvancedInventory
├── MultiBranch
├── Manufacturing
├── Restaurant
├── ECommerce
├── CloudBackup
├── Synchronization
└── EmployeeManagement
```

These modules should not be required by the basic POS.

---

# 23. Module Dependency Graph

Dependencies should form a directed graph.

A possible initial dependency structure:

```text
                         Platform.Core
                              │
                              ▼
                       Platform.Application
                              │
               ┌──────────────┴──────────────┐
               ▼                             ▼
          Catalog.Contracts             Other Contracts
               │
               ▼
          Catalog Module
               │
               ▼
        Inventory Contracts
               │
               ▼
        Inventory Module
               │
          ┌────┴────┐
          ▼         ▼
       Sales      Purchasing
          │
          ▼
         POS
```

---

# 24. Dependency Direction

The most important rule:

> Dependencies must point toward more fundamental abstractions.

Valid:

```text
UI
 ↓
Application
 ↓
Domain
```

Valid:

```text
Infrastructure
 ↓
Application
 ↓
Domain
```

Invalid:

```text
Domain
 ↓
Infrastructure
```

Invalid:

```text
Domain
 ↓
UI
```

Invalid:

```text
Core
 ↓
POS
```

---

# 25. Core Dependency Rule

The Core must never depend on business modules.

Invalid:

```text
Platform.Core
    ↓
Inventory
```

Invalid:

```text
Platform.Core
    ↓
POS
```

Invalid:

```text
Platform.Core
    ↓
Accounting
```

Instead:

```text
Inventory
    ↓
Platform.Core
```

---

# 26. Module-to-Module Dependency Rule

A module may depend on another module only when the dependency represents a genuine business relationship.

Example:

```text
POS
 ↓
Sales
```

is reasonable.

```text
Sales
 ↓
Inventory
```

is reasonable because completing a sale can affect stock.

But:

```text
Inventory
 ↓
POS
```

should not exist.

Inventory should not know that a POS application exists.

---

# 27. Contracts Rule

When Module A needs functionality from Module B:

```text
Module A
   ↓
Module B.Contracts
```

not:

```text
Module A
   ↓
Module B.Infrastructure
```

and not:

```text
Module A
   ↓
Module B.UI
```

This is one of the most important dependency rules.

---

# 28. Example: Sales and Inventory

Sales needs to reduce stock.

It should depend on an inventory contract:

```text
Sales.Application
       ↓
IInventoryStockService
       ↓
Inventory.Contracts
```

The actual implementation is supplied by Inventory.

Sales should not know:

* EF Core
* SQLite tables
* Inventory DbContext
* Inventory repositories

---

# 29. Example: POS and Sales

POS is primarily a user-facing interaction module.

The architecture should be:

```text
POS.UI
   ↓
POS.Application
   ↓
Sales.Contracts
   ↓
Sales
```

POS should not directly manipulate inventory.

The business operation remains in Sales.

---

# 30. Example: Reporting

Reporting should consume contracts/data exposed by other modules.

For example:

```text
Reporting
 ├── Sales.Contracts
 ├── Inventory.Contracts
 └── Purchasing.Contracts
```

Reporting should not directly access:

```text
SalesDbContext
InventoryDbContext
PurchasingDbContext
```

---

# 31. Database Architecture

SQLite is the local operational database.

The database belongs to the client installation.

Conceptually:

```text
Client
│
└── Data
    └── business.db
```

The database is not owned by the UI.

It is not owned by the POS.

It belongs to the local platform/data infrastructure.

---

# 32. Database Ownership

Although SQLite may physically be one database file, **logical ownership belongs to modules**.

For example:

```text
Catalog
├── Products
├── ProductVariants
├── Categories
└── Barcodes

Inventory
├── Stock
├── StockMovements
├── Warehouses
└── Locations

Sales
├── Sales
├── SaleItems
└── Returns

Purchasing
├── PurchaseOrders
├── Purchases
└── PurchaseItems
```

A module owns its tables and migrations.

---

# 33. Module Database Rule

A module must never directly modify another module's tables.

Invalid:

```text
Inventory
    ↓
UPDATE Sales
```

Invalid:

```text
POS
    ↓
UPDATE InventoryStock
```

Instead:

```text
POS
 ↓
Sales Application
 ↓
Inventory Contract
 ↓
Inventory
```

---

# 34. Database Transactions

Business operations involving multiple modules require transactional coordination.

Example:

```text
Complete Sale

1. Create sale
2. Record payment
3. Reduce inventory
4. Create stock movement
5. Commit
```

These operations should behave as one logical transaction.

Failure should not leave the database in an inconsistent state.

---

# 35. Transaction Boundary

The transaction boundary should be at the application/use-case level.

Example:

```text
CompleteSaleHandler
       │
       ├── Sales
       ├── Payments
       └── Inventory
              │
              ▼
          Transaction
```

The implementation must prevent partial completion.

---

# 36. Local Database Migrations

Each module owns its schema migrations.

Example:

```text
Inventory.Infrastructure
    └── Migrations

Sales.Infrastructure
    └── Migrations

Catalog.Infrastructure
    └── Migrations
```

The platform coordinates migration execution.

The platform should not contain the business schema of every module.

---

# 37. Module Installation

Installing a module involves more than copying a DLL.

The process is:

```text
Download package
      ↓
Verify signature
      ↓
Verify package hash
      ↓
Validate manifest
      ↓
Validate application compatibility
      ↓
Validate module dependencies
      ↓
Check license entitlement
      ↓
Install files
      ↓
Run database migration
      ↓
Register module
      ↓
Activate module
```

---

# 38. Module Manifest

Every module should contain metadata.

Conceptually:

```text
Module Manifest

ModuleId
Name
Version
Publisher
MinimumPlatformVersion
MaximumPlatformVersion
Dependencies
RequiredFeatures
DatabaseVersion
PackageHash
Signature
```

Example:

```text
ModuleId:
    accounting

Version:
    1.2.0

Dependencies:
    catalog >= 1.0
    sales >= 2.0

MinimumPlatformVersion:
    2.0.0
```

---

# 39. Module Lifecycle

A module has a lifecycle.

```text
Available
   ↓
Downloaded
   ↓
Verified
   ↓
Installed
   ↓
Registered
   ↓
Licensed
   ↓
Enabled
   ↓
Running
```

It can later become:

```text
Disabled
Suspended
Updated
Uninstalled
```

---

# 40. Module Interface

The platform should define a module contract.

Conceptually:

```text
IModule

ModuleId
Version
Dependencies
ConfigureServices()
RegisterUI()
Initialize()
Start()
Stop()
```

The exact interface will be designed during implementation.

The important rule is that modules communicate with the platform through a controlled lifecycle.

---

# 41. Module Dependency Resolution

If a customer installs:

```text
Accounting
```

and Accounting requires:

```text
Sales
Inventory
```

the Module Host must resolve the dependency graph.

```text
Accounting
   ↓
Sales
   ↓
Inventory
   ↓
Catalog
   ↓
Platform
```

The installer must refuse incompatible dependency graphs.

Circular dependencies must be rejected.

---

# 42. Feature vs Module

A distinction must be maintained.

A **module** is a technical unit.

A **feature** is a commercial/capability unit.

One module can contain multiple features.

Example:

```text
Accounting Module

Features:
├── General Ledger
├── Accounts Payable
├── Accounts Receivable
└── Financial Reports
```

The license may grant the entire module or individual capabilities depending on the commercial model.

---

# 43. Feature Registry

The platform should have a feature registry.

Conceptually:

```text
FeatureId
Name
ModuleId
RequiredLicense
Status
```

Example:

```text
accounting.general-ledger
accounting.accounts-payable
inventory.batch-tracking
inventory.expiration
cloud.backup
```

---

# 44. Entitlement System

A license contains entitlements.

Conceptually:

```text
License
│
├── LicenseId
├── CustomerId
├── InstallationLimit
├── Expiration
├── Status
│
└── Entitlements
      ├── POS
      ├── Inventory
      ├── Purchasing
      ├── Accounting
      └── CloudBackup
```

The client uses the local signed license to determine what the customer is entitled to use.

---

# 45. Licensing Architecture

The licensing system consists of:

```text
Client
│
└── Client.Licensing
          │
          │ HTTPS
          ▼
     Licensing API
          │
          ▼
    License Database
```

The cloud license server is authoritative for license state.

The client is authoritative for **offline operational behavior**, according to the signed lease it possesses.

---

# 46. License Server Projects

The cloud solution can eventually contain:

```text
Cloud/
│
├── LicenseServer
│
├── LicenseServer.Application
│
├── LicenseServer.Domain
│
├── LicenseServer.Infrastructure
│
├── UpdateServer
│
├── BackupServer
│
└── AdminPortal
```

These should initially remain part of one deployable backend where practical.

---

# 47. License Domain

The licensing backend should model:

```text
Customer
License
Installation
Entitlement
Plan
Feature
Module
LicenseLease
Activation
```

Relationships:

```text
Customer
   │
   └── License
          │
          ├── Entitlements
          │
          └── Installations
```

---

# 48. License Activation Flow

A new installation:

```text
User installs application
        ↓
Application generates InstallationId
        ↓
User enters license
        ↓
Client contacts License API
        ↓
Server validates license
        ↓
Server validates installation limit
        ↓
Server registers installation
        ↓
Server creates signed license lease
        ↓
Client stores lease
        ↓
Application activates
```

---

# 49. Offline License Verification

When the application starts without Internet:

```text
Application
     ↓
Read local license lease
     ↓
Verify digital signature
     ↓
Check expiration
     ↓
Check installation identity
     ↓
Determine license state
     ↓
Load permitted modules
```

No Internet request is required for ordinary offline operation.

---

# 50. Online License Verification

When Internet is available:

```text
Client
   ↓
License API
   ↓
Validate current license
   ↓
Check status
   ↓
Retrieve current entitlements
   ↓
Issue renewed lease
   ↓
Client stores new lease
```

The application then continues operating using the new lease.

---

# 51. License States

The client and server should support explicit states:

```text
Active
GracePeriod
Expired
Suspended
Revoked
```

Each state has a defined behavior.

The application must not invent its own interpretation of server state.

---

# 52. License Expiration

The client should use an explicit license policy.

Example:

```text
Active
   ↓
Expiration
   ↓
Grace Period
   ↓
Restricted
```

The system should never:

* Delete data
* Corrupt data
* Encrypt customer data as punishment
* Destroy the database
* Hide customer data permanently

The customer's business data must remain recoverable.

---

# 53. Remote Suspension

The vendor may suspend a license.

The change becomes effective when the client can communicate with the license server or when an existing lease expires, depending on the lease policy.

This is necessary because:

> A completely offline computer cannot receive a new remote state.

This is an architectural limitation, not something that can be solved through software alone.

---

# 54. Installation Identity

Every installation receives a generated identifier:

```text
InstallationId
```

This is not simply a hardware fingerprint.

The server associates:

```text
Customer
   ↓
License
   ↓
Installation
```

An installation record may contain:

```text
InstallationId
LicenseId
ApplicationVersion
CreatedAt
LastSeen
Status
```

Only the minimum useful telemetry should be collected.

---

# 55. Installation Limits

A license may define:

```text
MaximumInstallations = 3
```

The server can enforce this during activation.

Example:

```text
License
Maximum = 3

Installation 1 ✓
Installation 2 ✓
Installation 3 ✓
Installation 4 ✗
```

The vendor may provide an administration interface to deactivate old installations.

---

# 56. Download Tracking

The system should distinguish:

### Download tracking

```text
Server
 ↓
Installer download
```

from:

### Installation tracking

```text
Installer
 ↓
Installation
 ↓
Activation
```

From the server's perspective, activation is more meaningful than simply counting downloads.

A copied installer that never connects to the Internet cannot be reliably tracked.

---

# 57. Update Architecture

There are two types of updates.

## Core Update

Updates:

```text
Client Application
Platform
Module Host
Updater
```

## Module Update

Updates:

```text
Individual Module
```

These should be independently versioned.

---

# 58. Update Server

The update platform stores:

```text
Application
Modules
Versions
Packages
Manifests
Hashes
Signatures
Release Notes
Compatibility Information
```

Conceptually:

```text
Update Server
│
├── Client
│   ├── 2.0.0
│   └── 2.1.0
│
├── Inventory
│   ├── 1.0.0
│   └── 1.1.0
│
└── Accounting
    ├── 1.0.0
    └── 1.2.0
```

---

# 59. Update Flow

Client checks:

```text
Current Version
      ↓
Update API
      ↓
Available Updates
      ↓
Filter by:
    License
    Compatibility
    Dependencies
      ↓
Download
      ↓
Verify
      ↓
Install
```

---

# 60. Package Security

Every package should have:

```text
Package
Hash
Digital Signature
Publisher
Version
Compatibility
```

The client should verify the signature before executing or installing the package.

A package with an invalid signature must be rejected.

---

# 61. Update Rollback

Updates can fail.

Therefore the updater should support rollback.

Conceptually:

```text
Current Version
      ↓
Create restore point
      ↓
Install update
      ↓
Validate
      ↓
Success
```

If installation fails:

```text
Failed
   ↓
Rollback
   ↓
Restore previous version
```

---

# 62. Database Migration During Updates

A module update may require a database migration.

Example:

```text
Inventory 1.0
     ↓
Update
     ↓
Inventory 1.1
     ↓
Migration
     ↓
Database 1.1
```

Migration execution must be controlled and validated.

The updater must not simply replace application files and assume the database is compatible.

---

# 63. Cloud Backup Architecture

Cloud Backup is an optional module.

The client:

```text
SQLite
   ↓
Backup Service
   ↓
Encryption/Packaging
   ↓
Cloud Backup API
   ↓
Object Storage
```

The core POS does not depend on the Cloud Backup module.

---

# 64. Backup Independence

If Cloud Backup is disabled:

```text
POS
Inventory
Sales
Purchasing
```

must continue working.

The cloud backup feature must not become a hidden dependency of the local application.

---

# 65. Synchronization

Synchronization is a separate subsystem.

It should not be treated as "backup with two computers."

Synchronization requires:

* Change tracking
* Conflict detection
* Conflict resolution
* Identity
* Ordering
* Retry
* Offline queues

Therefore it should remain an independent future module.

---

# 66. Cloud API Boundary

The desktop application should communicate with cloud services through explicit clients.

Example:

```text
Client
│
├── ILicenseClient
├── IUpdateClient
├── IBackupClient
└── ISyncClient
```

Implementations:

```text
HttpLicenseClient
HttpUpdateClient
HttpBackupClient
HttpSyncClient
```

Business modules should not directly construct `HttpClient` requests to cloud endpoints.

---

# 67. Offline/Online Boundary

The architecture must make the Internet boundary explicit.

Bad:

```text
SalesService
    ↓
HTTP
    ↓
Server
```

Better:

```text
SalesService
    ↓
Local Database
```

Optional:

```text
Backup Module
    ↓
Cloud
```

And:

```text
License Client
    ↓
Cloud
```

The core business engine should remain local.

---

# 68. Configuration

Configuration should be separated into:

### Application configuration

```text
App settings
UI settings
Printer settings
Localization
```

### Business configuration

```text
Currency
Tax settings
Business information
Inventory settings
```

### License configuration

```text
License
Installation
Lease
Entitlements
```

### Cloud configuration

```text
Server endpoints
Backup configuration
```

Sensitive secrets must not be stored as plain text configuration values.

---

# 69. Authentication and Authorization

Authentication answers:

> Who is the user?

Authorization answers:

> What can the user do?

These must remain separate.

Example:

```text
User
 ↓
Authentication
 ↓
Identity
 ↓
Roles / Permissions
 ↓
Authorization
```

---

# 70. Permission Architecture

Permissions should be capability-oriented.

Example:

```text
products.create
products.edit
products.delete

inventory.adjust
inventory.transfer

sales.create
sales.return

reports.view

users.manage
settings.manage
```

Modules can register their own permissions.

Example:

```text
Accounting
├── accounting.accounts.view
├── accounting.journal.create
└── accounting.reports.view
```

---

# 71. Audit Architecture

The platform should expose an audit contract:

```text
IAuditService
```

Modules can record important actions.

Example:

```text
User
Action
Entity
EntityId
Timestamp
Metadata
```

The Audit implementation belongs to the platform.

Business modules should not implement separate incompatible audit systems.

---

# 72. Hardware Abstraction

Hardware must be abstracted.

Example:

```text
IBarcodeScanner
IReceiptPrinter
ILabelPrinter
ICashDrawer
IScale
```

The POS depends on interfaces.

Concrete hardware drivers are infrastructure implementations.

This allows different hardware manufacturers to be supported later.

---

# 73. UI Architecture

The UI should be modular.

The platform provides:

```text
Navigation
Menu
Dashboard
Notifications
Dialogs
Themes
Localization
```

Modules contribute:

```text
Inventory
 ├── Inventory Dashboard
 ├── Stock
 ├── Adjustments
 └── Transfers
```

POS contributes:

```text
POS
 ├── Register
 ├── Cart
 └── Payment
```

The platform should compose these contributions.

---

# 74. Business Rules

Business rules belong to Domain/Application layers.

They must not be implemented in:

* WPF code-behind
* ViewModels
* SQL queries alone
* Controllers
* HTTP clients

Example:

```text
"Cannot sell more stock than available"
```

is a business rule.

It belongs to the business/application domain, not the UI.

---

# 75. Generic Inventory Architecture

The inventory architecture must support capabilities such as:

```text
Basic Stock
Variants
Units
Unit Conversion
Warehouses
Locations
Batches
Expiration
Serial Numbers
Weighted Products
Composite Products
```

However, these capabilities should not all become mandatory properties on every product.

The architecture should support optional capabilities.

---

# 76. Industry Modules

Industry-specific functionality should remain outside the generic core.

Example:

```text
Generic Inventory
       │
       ├── Pharmacy Module
       │      └── Pharmacy-specific behavior
       │
       ├── Restaurant Module
       │      └── Restaurant-specific behavior
       │
       └── Manufacturing Module
              └── Manufacturing-specific behavior
```

This prevents industry-specific concepts from contaminating the generic model.

---

# 77. Dependency Rules Summary

The following rules are mandatory.

### Rule 1

Core never depends on business modules.

### Rule 2

Domain never depends on infrastructure.

### Rule 3

Domain never depends on UI.

### Rule 4

Modules never depend directly on another module's Infrastructure project.

### Rule 5

Cross-module communication occurs through contracts.

### Rule 6

UI never directly accesses the database.

### Rule 7

Business logic never depends on HTTP.

### Rule 8

Cloud availability must never be required for ordinary offline business operations.

### Rule 9

Modules must declare dependencies explicitly.

### Rule 10

Circular module dependencies are forbidden.

### Rule 11

A feature must not modify another module's database tables directly.

### Rule 12

The licensing system must not contain business logic.

### Rule 13

The business system must not contain licensing-server implementation details.

### Rule 14

The updater must verify packages before installation.

### Rule 15

Data must never be destroyed because of license state.

---

# 78. Dependency Direction Diagram

The intended dependency direction is:

```text
                    UI
                    │
                    ▼
              Application
                    │
                    ▼
                  Domain
                    ▲
                    │
             Infrastructure


          Module A
             │
             ▼
      Module B.Contracts
             │
             ▼
          Module B
```

The following direction is forbidden:

```text
Domain
  ↓
Infrastructure
```

and:

```text
Core
  ↓
Business Module
```

and:

```text
Business Module
  ↓
UI of another Module
```

---

# 79. Full Solution Structure

The eventual solution should resemble:

```text
GenericPOS.sln
│
├── src/
│
│   ├── Platform/
│   │   │
│   │   ├── Platform.Core
│   │   ├── Platform.Contracts
│   │   ├── Platform.Application
│   │   └── Platform.Infrastructure
│   │
│   ├── Client/
│   │   │
│   │   ├── Client.Desktop
│   │   ├── Client.Host
│   │   ├── Client.ModuleHost
│   │   ├── Client.Licensing
│   │   └── Client.Updater
│   │
│   ├── Modules/
│   │   │
│   │   ├── Catalog/
│   │   │   ├── Catalog.Domain
│   │   │   ├── Catalog.Application
│   │   │   ├── Catalog.Contracts
│   │   │   ├── Catalog.Infrastructure
│   │   │   └── Catalog.UI
│   │   │
│   │   ├── Inventory/
│   │   │   ├── Inventory.Domain
│   │   │   ├── Inventory.Application
│   │   │   ├── Inventory.Contracts
│   │   │   ├── Inventory.Infrastructure
│   │   │   └── Inventory.UI
│   │   │
│   │   ├── Sales/
│   │   │   ├── Sales.Domain
│   │   │   ├── Sales.Application
│   │   │   ├── Sales.Contracts
│   │   │   ├── Sales.Infrastructure
│   │   │   └── Sales.UI
│   │   │
│   │   ├── POS/
│   │   │   ├── POS.Application
│   │   │   ├── POS.Contracts
│   │   │   └── POS.UI
│   │   │
│   │   ├── Purchasing/
│   │   ├── Customers/
│   │   ├── Suppliers/
│   │   ├── Pricing/
│   │   ├── Payments/
│   │   ├── Reporting/
│   │   ├── Users/
│   │   ├── Audit/
│   │   └── CashManagement/
│   │
│   ├── OptionalModules/
│   │   │
│   │   ├── Accounting/
│   │   ├── Loyalty/
│   │   ├── AdvancedReports/
│   │   ├── AdvancedInventory/
│   │   ├── MultiBranch/
│   │   ├── Manufacturing/
│   │   ├── Restaurant/
│   │   ├── ECommerce/
│   │   ├── CloudBackup/
│   │   ├── Synchronization/
│   │   └── EmployeeManagement/
│   │
│   └── Cloud/
│       │
│       ├── LicenseServer
│       ├── LicenseServer.Application
│       ├── LicenseServer.Domain
│       ├── LicenseServer.Infrastructure
│       ├── UpdateServer
│       ├── BackupServer
│       └── AdminPortal
│
├── tests/
│   ├── Platform.Tests
│   ├── Integration.Tests
│   ├── Module.Tests
│   └── Architecture.Tests
│
└── tools/
    ├── Installer
    ├── ModulePackager
    └── UpdatePublisher
```

---

# 80. Architecture Tests

The architecture itself should be tested.

For example, tests should ensure:

```text
Platform.Core
    cannot reference
        Inventory

Inventory.Domain
    cannot reference
        Inventory.Infrastructure

Sales
    cannot reference
        Inventory.Infrastructure

Domain
    cannot reference
        WPF
```

Architecture tests can automatically detect violations.

This is important because dependency rules are easy to violate as the project grows.

---

# 81. Testing Strategy

Each module should have:

```text
Unit Tests
Integration Tests
Architecture Tests
```

For example:

```text
Inventory/
├── Inventory.Domain
├── Inventory.Application
├── Inventory.Infrastructure
└── Tests
    ├── Inventory.Domain.Tests
    ├── Inventory.Application.Tests
    └── Inventory.Integration.Tests
```

The platform should also have cross-module integration tests.

---

# 82. First Vertical Slice

Before implementing the entire system, the architecture should be proven with one small end-to-end workflow:

```text
Create Product
      ↓
Add Stock
      ↓
Open POS
      ↓
Add Product
      ↓
Complete Sale
      ↓
Record Payment
      ↓
Reduce Stock
      ↓
Record Stock Movement
      ↓
Save Transaction
```

This proves:

* Module communication
* Database access
* Transactions
* Dependency injection
* UI/application separation
* Inventory/sales interaction

before the project becomes large.

---

# 83. Architecture Implementation Order

The architecture should be implemented in this order.

## Stage 1 — Solution Foundation

```text
Platform.Core
Platform.Contracts
Platform.Application
Platform.Infrastructure
```

---

## Stage 2 — Client Host

```text
Client.Host
Client.ModuleHost
Client.Desktop
```

Implement:

* Dependency injection
* Configuration
* Logging
* Module discovery
* Startup/shutdown

---

## Stage 3 — Database Foundation

Implement:

```text
SQLite
EF Core
DbContext infrastructure
Migration infrastructure
Transactions
```

Do not implement complete business entities yet.

---

## Stage 4 — Module Contract

Implement:

```text
IModule
IModuleManifest
Module dependency model
Feature identifiers
Module lifecycle
```

---

## Stage 5 — First Modules

Implement minimal versions of:

```text
Catalog
Inventory
Sales
POS
```

Only enough to prove the architecture.

---

## Stage 6 — Licensing

Implement:

```text
License Server
License Client
Installation identity
Signed license
Entitlements
Offline lease
```

---

## Stage 7 — Update System

Implement:

```text
Update Server
Package manifest
Digital signatures
Module package
Updater
Rollback
```

---

## Stage 8 — Additional Business Modules

Then gradually implement:

```text
Purchasing
Customers
Suppliers
Pricing
Payments
Reporting
Users
Audit
Cash Management
```

---

# 84. What We Must Not Do Yet

Before the architecture is approved, we should not start implementing:

* Complete Product entity
* Complete Inventory entity
* Complete POS UI
* Accounting
* Cloud synchronization
* Complex reporting
* Manufacturing
* Restaurant features

We first need to prove the architecture.

---

# 85. Architectural Success Criteria

The architecture is considered successful when we can demonstrate:

### Module independence

```text
Remove Accounting
        ↓
Core still works
```

### Offline operation

```text
Disable Internet
        ↓
Create sale
        ↓
Inventory updates
```

### Feature licensing

```text
License without Accounting
        ↓
Accounting unavailable
```

### Feature activation

```text
License updated
        ↓
Accounting installed
        ↓
Accounting becomes available
```

### Secure updates

```text
Invalid package
        ↓
Rejected
```

### Database boundaries

```text
Inventory
    cannot directly modify
Sales tables
```

### Cloud independence

```text
Cloud unavailable
        ↓
POS continues operating
```

---

# 86. Final Architectural Model

The complete architecture can be summarized as:

```text
                         CLOUD
                           │
              ┌────────────┼────────────┐
              │            │            │
          Licensing     Updates      Backup
              │            │            │
              └────────────┼────────────┘
                           │
                         HTTPS
                           │
                           ▼
┌─────────────────────────────────────────────────────┐
│                 DESKTOP APPLICATION                 │
│                                                     │
│                    Client Host                      │
│                         │                           │
│                    Module Host                      │
│                         │                           │
│       ┌─────────────────┼─────────────────┐         │
│       │                 │                 │         │
│    Platform          Modules          Optional      │
│       │                 │             Modules      │
│       │                 │                 │         │
│       │       ┌─────────┼─────────┐       │         │
│       │       │         │         │       │         │
│       │    Catalog   Inventory   Sales    │         │
│       │       │         │         │       │         │
│       │       └─────────┼─────────┘       │         │
│       │                 │                 │         │
│       │                POS                │         │
│       │                                   │         │
│       └─────────────────┼─────────────────┘         │
│                         │                           │
│                    Data Access                      │
│                         │                           │
│                      SQLite                         │
│                                                     │
└─────────────────────────────────────────────────────┘
```

---

# 87. Core Architectural Principle

The most important rule of the entire project is:

> **The platform owns the infrastructure; modules own their business capabilities; contracts control communication; the local database powers offline operations; and the cloud provides optional coordination, licensing, updates, and backup.**

This principle should guide every future architectural decision.

---

# 88. Final Dependency Rule

The desired dependency direction is:

```text
                 ┌──────────────┐
                 │     UI       │
                 └──────┬───────┘
                        │
                        ▼
                 ┌──────────────┐
                 │ Application  │
                 └──────┬───────┘
                        │
                        ▼
                 ┌──────────────┐
                 │    Domain    │
                 └──────────────┘
                        ▲
                        │
                 ┌──────┴───────┐
                 │Infrastructure│
                 └──────────────┘


Cross-module:

 Module A
    │
    ▼
Module B.Contracts
    │
    ▼
 Module B


Never:

 Core → Module
 Domain → Infrastructure
 Domain → UI
 Module → Module.Infrastructure
 Module → Module.UI
 Business → HTTP
 UI → Database
```

This becomes the **architectural constitution of the project**.

Every new module, feature, project, interface, database table, cloud service, and dependency should be evaluated against these rules before it is introduced.
