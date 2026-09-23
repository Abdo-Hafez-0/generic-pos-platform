# Generic Offline-First Inventory & POS Platform

# Module Map & Dependency Specification

**Status:** Architecture Design — Step 2
**Previous Step:** Complete Architecture / Solution Structure
**Next Step:** Project Creation & Architecture Skeleton

---

# 1. Purpose

This document defines the exact responsibilities and dependency relationships of the platform's modules.

The goal is to answer:

* What belongs to each module?
* What does each module own?
* Which modules may depend on which?
* Which modules must remain completely independent?
* What contracts cross module boundaries?
* Which modules are required for the MVP?
* Which modules are optional?
* Which dependencies are forbidden?

This document is the boundary between the **architecture** and the eventual implementation.

---

# 2. Core Principle

Every module must answer one question:

> **What business capability does this module own?**

A module should not exist merely because a group of classes seems convenient to place together.

For every module:

```text
Module
│
├── Owns a business capability
├── Owns its domain rules
├── Owns its data
├── Exposes selected contracts
└── Hides its implementation
```

---

# 3. Module Categories

The platform has four categories.

```text
Platform
│
├── Foundation
│
├── Core Business Modules
│
├── Supporting Modules
│
└── Optional / Commercial Modules
```

---

# 4. Platform Modules

These are not business modules.

They provide capabilities required by the entire application.

```text
Platform
│
├── Core
├── Contracts
├── Application
└── Infrastructure
```

The Platform must never depend on business modules.

---

# 5. Core Business Modules

The initial business platform consists of:

```text
Catalog
Inventory
Sales
POS
Purchasing
Customers
Suppliers
Pricing
Payments
```

These represent the primary operational system.

---

# 6. Supporting Modules

```text
Reporting
Users
Audit
CashManagement
```

These support the primary business modules.

---

# 7. Optional Modules

```text
Accounting
Loyalty
AdvancedInventory
AdvancedReports
MultiBranch
Manufacturing
Restaurant
ECommerce
CloudBackup
Synchronization
EmployeeManagement
```

They must not be required by the generic POS core.

---

# 8. Catalog Module

## Responsibility

Catalog owns the definition of what the business sells or stores.

It answers:

> "What is this product?"

---

## Catalog Owns

```text
Product
ProductVariant
Category
Brand
Barcode
Unit
ProductImage
ProductAttribute
ProductStatus
```

Depending on the final domain model, some of these may be merged or separated.

---

## Catalog Does NOT Own

Catalog does not own:

```text
Stock
StockQuantity
WarehouseStock
Sales
PurchaseOrders
Payments
Customers
```

For example:

```text
Product
    ✓ Catalog

CurrentStock
    ✗ Catalog
```

---

# 9. Catalog Responsibilities

Catalog handles:

```text
Create Product
Edit Product
Deactivate Product
Create Category
Assign Barcode
Manage Product Variants
Manage Units
Manage Product Metadata
```

---

# 10. Catalog Contracts

Other modules may consume read-oriented contracts.

Example:

```text
IProductReader
IProductLookup
IProductBarcodeResolver
```

Potential operations:

```text
GetProduct(productId)
FindByBarcode(barcode)
GetProductVariant(productId, variantId)
```

The actual Catalog repository remains private.

---

# 11. Inventory Module

## Responsibility

Inventory owns the physical stock state of the business.

It answers:

> "How many units do we currently have, and where are they?"

---

# 12. Inventory Owns

```text
StockItem
StockMovement
Warehouse
Location
StockAdjustment
StockTransfer
InventoryBalance
```

Future capabilities:

```text
Batch
SerialNumber
Expiration
Lot
```

may be implemented inside Inventory or specialized modules.

---

# 13. Inventory Does NOT Own

Inventory does not own:

```text
Product definition
Sales
Purchase orders
Payments
Customers
POS screens
```

Inventory knows about products through identifiers/contracts.

---

# 14. Inventory Responsibilities

```text
Receive Stock
Increase Stock
Decrease Stock
Adjust Stock
Transfer Stock
Check Availability
Record Stock Movement
Calculate Current Balance
```

---

# 15. Inventory Contracts

Inventory exposes contracts such as:

```text
IInventoryReader
IStockAvailabilityChecker
IStockMovementReader
IStockAdjustmentService
IStockReservationService
```

The exact contract set should remain minimal.

Do not expose the entire Inventory application layer.

---

# 16. Sales Module

## Responsibility

Sales owns completed commercial sales transactions.

It answers:

> "What did the business sell?"

---

# 17. Sales Owns

```text
Sale
SaleItem
SaleStatus
Return
ReturnItem
SalesTransaction
```

Potentially:

```text
DiscountApplication
TaxSnapshot
PriceSnapshot
```

where these represent values captured as part of the historical sale.

---

# 18. Important Historical Data Rule

A completed sale should preserve the values that existed at the time of the transaction.

For example:

```text
Product Current Price
       ≠
Historical Sale Price
```

If a product's price changes tomorrow, yesterday's sale must not change.

Therefore:

```text
SaleItem
├── ProductId
├── Quantity
├── UnitPrice
├── Discount
└── Tax
```

contains the transaction snapshot required by the business.

---

# 19. Sales Does NOT Own

Sales does not own:

```text
Current stock
Product master data
Physical warehouse
Payment processing implementation
POS UI
```

---

# 20. Sales Dependencies

Sales requires access to:

```text
Catalog.Contracts
Inventory.Contracts
Pricing.Contracts
Payments.Contracts
```

But Sales must not access their infrastructure implementations.

---

# 21. POS Module

## Responsibility

POS is the operational user interface/workflow for selling.

It answers:

> "How does the cashier perform a sale?"

This distinction is critical.

---

# 22. POS Does NOT Own Sales

POS should not become the owner of:

```text
Sale
SaleItem
Return
SalesTransaction
```

Those belong to Sales.

POS orchestrates the cashier experience.

---

# 23. POS Responsibilities

```text
Open Register
Create Cart
Add Item
Remove Item
Change Quantity
Apply Allowed Discount
Select Customer
Select Payment
Complete Sale
Print Receipt
Open Cash Drawer
```

---

# 24. POS Dependency

The intended relationship:

```text
POS
 │
 ▼
Sales.Contracts
```

POS may also consume:

```text
Catalog.Contracts
Pricing.Contracts
Payments.Contracts
Inventory.Contracts
```

but should not manipulate those modules' databases.

---

# 25. Purchasing Module

## Responsibility

Purchasing owns the process of acquiring goods from suppliers.

It answers:

> "What are we buying from suppliers?"

---

# 26. Purchasing Owns

```text
PurchaseOrder
PurchaseOrderItem
GoodsReceipt
Purchase
PurchaseItem
PurchaseStatus
```

Potentially:

```text
PurchaseReturn
```

---

# 27. Purchasing Dependencies

Purchasing may consume:

```text
Catalog.Contracts
Inventory.Contracts
Suppliers.Contracts
Pricing.Contracts
```

Example:

```text
Goods Receipt
      ↓
Inventory
      ↓
Stock Increased
```

Purchasing does not directly update Inventory tables.

---

# 28. Customers Module

## Responsibility

Customer relationship data.

```text
Customer
CustomerAddress
CustomerContact
CustomerStatus
CustomerGroup
```

Potential future capabilities:

```text
CustomerCredit
CustomerBalance
CustomerNotes
```

Accounting-related balances should eventually belong to Accounting rather than Customers.

---

# 29. Customer Contracts

Other modules can consume:

```text
ICustomerReader
ICustomerLookup
```

Example:

```text
POS
 ↓
ICustomerLookup
 ↓
Customer
```

POS should not directly query the Customers database.

---

# 30. Suppliers Module

Suppliers is the equivalent of Customers for procurement.

It owns:

```text
Supplier
SupplierContact
SupplierAddress
SupplierStatus
```

Purchasing consumes:

```text
ISupplierReader
ISupplierLookup
```

---

# 31. Pricing Module

Pricing deserves its own boundary because price calculation can become surprisingly complex.

It answers:

> "What price should this product have for this transaction?"

---

# 32. Pricing Owns

Potentially:

```text
PriceList
Price
PriceRule
DiscountRule
Promotion
CustomerPrice
QuantityPrice
```

Future:

```text
TimeBasedPrice
BranchPrice
ChannelPrice
```

---

# 33. Pricing Does NOT Own Sales

Pricing determines the price.

Sales records the price actually used.

Therefore:

```text
Pricing
   ↓
Calculated Price
   ↓
Sales
   ↓
Historical Price Snapshot
```

---

# 34. Payments Module

Payments answers:

> "How was the transaction paid?"

It owns payment-related concepts.

```text
Payment
PaymentMethod
PaymentStatus
PaymentAllocation
Refund
```

Potential payment methods:

```text
Cash
Card
BankTransfer
Credit
Other
```

Payment-specific integrations should be infrastructure implementations behind contracts.

---

# 35. Payments Dependency

Sales may depend on:

```text
IPaymentService
```

rather than a concrete payment implementation.

For example:

```text
Sales
 ↓
IPaymentService
 ↓
CashPaymentService
```

or:

```text
Sales
 ↓
IPaymentService
 ↓
CardPaymentService
```

---

# 36. Reporting Module

Reporting is a consumer of business information.

It should not own the source business data.

Example:

```text
Reporting
│
├── Sales.Contracts
├── Inventory.Contracts
├── Purchasing.Contracts
└── CashManagement.Contracts
```

---

# 37. Reporting Rule

Reporting must not become:

```text
Reporting
 ↓
All DbContexts
```

That would destroy module boundaries.

Instead:

```text
Reporting
 ↓
Public Contracts / Reporting Queries
```

---

# 38. Users Module

Users owns local application identity.

```text
User
Role
Permission
RolePermission
UserRole
```

Authentication:

```text
Who are you?
```

Authorization:

```text
What can you do?
```

These remain separate concerns.

---

# 39. Audit Module

Audit owns the historical record of important actions.

```text
AuditEntry
```

Example:

```text
User: John
Action: Inventory Adjustment
Entity: StockItem
EntityId: 123
Timestamp: ...
```

Other modules publish audit information.

Audit should not contain business rules.

---

# 40. Cash Management Module

Cash Management owns physical/operational cash movements.

```text
CashRegister
CashSession
CashMovement
CashAdjustment
CashOpening
CashClosing
```

This is different from Payments.

### Payments

```text
How did the customer pay?
```

### Cash Management

```text
What happened to the physical cash register?
```

---

# 41. Critical Dependency Graph

The primary operational dependency graph should be:

```text
                    Platform
                       │
                       ▼
                    Catalog
                       │
                       ▼
                   Inventory
                       │
            ┌──────────┴──────────┐
            ▼                     ▼
         Sales                Purchasing
            │                     │
            │                     │
            ▼                     ▼
           POS                 Suppliers
```

However, the arrows above represent **business relationships**, not direct implementation references.

Actual code references should use Contracts.

---

# 42. Actual Code Dependency Graph

The preferred dependency graph is:

```text
                 Platform.Core
                       ▲
                       │
              Platform.Contracts
                       ▲
                       │
             Platform.Application
                       ▲
                       │
                Module Contracts
```

For modules:

```text
POS.Application
      │
      ├──────────► Sales.Contracts
      ├──────────► Catalog.Contracts
      ├──────────► Pricing.Contracts
      └──────────► Payments.Contracts
```

Sales:

```text
Sales.Application
      │
      ├──────────► Catalog.Contracts
      ├──────────► Inventory.Contracts
      ├──────────► Pricing.Contracts
      └──────────► Payments.Contracts
```

Purchasing:

```text
Purchasing.Application
      │
      ├──────────► Catalog.Contracts
      ├──────────► Inventory.Contracts
      └──────────► Suppliers.Contracts
```

---

# 43. The Golden Rule

Never:

```text
POS
 ↓
Inventory.Infrastructure
```

Never:

```text
Sales
 ↓
Inventory.Infrastructure
```

Never:

```text
Purchasing
 ↓
Inventory.Infrastructure
```

Always:

```text
Module A
 ↓
Module B.Contracts
 ↓
Module B Implementation
```

---

# 44. Database Ownership Graph

Logical database ownership:

```text
Catalog
 └── Product-related tables

Inventory
 └── Stock-related tables

Sales
 └── Sales-related tables

Purchasing
 └── Purchasing-related tables

Customers
 └── Customer-related tables

Suppliers
 └── Supplier-related tables

Pricing
 └── Pricing-related tables

Payments
 └── Payment-related tables

Users
 └── User-related tables

Audit
 └── Audit-related tables

CashManagement
 └── Cash-related tables
```

A module cannot modify another module's tables.

---

# 45. Cross-Module Data

Suppose a Sale contains:

```text
ProductId = 123
```

Sales does not need to duplicate the entire Product entity.

Instead:

```text
SaleItem
    ProductId
```

and, where required:

```text
ProductSnapshot
```

for historical information.

This prevents unnecessary coupling.

---

# 46. Cross-Module Entity Rule

Never pass another module's domain entity across a module boundary.

Bad:

```text
Sales
 ↓
Inventory.StockItem
```

Good:

```text
Sales
 ↓
IInventoryAvailability
 ↓
StockAvailabilityResult
```

Contracts should use:

* IDs
* DTOs
* value objects
* explicit result types

rather than leaking domain entities.

---

# 47. Dependency Matrix

| Module     |  Catalog | Inventory |    Sales | Purchasing | Customers | Suppliers |  Pricing | Payments |
| ---------- | -------: | --------: | -------: | ---------: | --------: | --------: | -------: | -------: |
| Catalog    |        — |        No |       No |         No |        No |        No |       No |       No |
| Inventory  | Contract |         — |       No |   Contract |        No |        No |       No |       No |
| Sales      | Contract |  Contract |        — |         No |  Contract |        No | Contract | Contract |
| POS        | Contract |  Contract | Contract |         No |  Contract |        No | Contract | Contract |
| Purchasing | Contract |  Contract |       No |          — |        No |  Contract | Contract | Contract |
| Customers  |       No |        No |       No |         No |         — |        No |       No |       No |
| Suppliers  |       No |        No |       No |   Contract |        No |         — |       No |       No |
| Pricing    | Contract |        No |       No |         No |  Optional |  Optional |        — |       No |
| Payments   |       No |        No | Contract |   Contract |  Optional |        No |       No |        — |

`Contract` means the dependency is allowed only through the public contract.

`No` means there should be no direct dependency.

---

# 48. Important Correction: POS and Inventory

POS may need Inventory information to display availability.

However:

```text
POS
 ↓
Inventory.Contracts
```

does not mean POS owns stock behavior.

POS merely asks:

```text
Is this product available?
```

The actual stock mutation happens through the Sales workflow.

---

# 49. Important Correction: Sales and Payments

Payment processing should not be hidden inside POS.

The business operation is:

```text
Complete Sale
```

and payment is part of that operation.

Therefore:

```text
POS
 ↓
Sales
 ↓
Payments
```

is preferable to:

```text
POS
 ├── Sales
 └── Payments
```

for the actual completion workflow.

POS remains the UI/application boundary.

---

# 50. Complete Sale Workflow

The intended architecture becomes:

```text
Cashier
   │
   ▼
POS
   │
   ▼
CompleteSale
   │
   ├──► Catalog
   │
   ├──► Pricing
   │
   ├──► Inventory
   │
   ├──► Payments
   │
   └──► Sales
```

All operations participate in the appropriate application transaction.

---

# 51. Purchase Workflow

```text
User
 │
 ▼
Purchasing
 │
 ├──► Supplier
 │
 ├──► Catalog
 │
 └──► Inventory
          │
          ▼
      Stock Increase
```

---

# 52. Return Workflow

A return should be owned by Sales.

```text
POS
 ↓
Sales
 ↓
Validate Original Sale
 ↓
Create Return
 ↓
Payments
 ↓
Inventory
 ↓
Stock Returned
```

POS does not implement return business rules.

---

# 53. Reporting Dependency Strategy

There are two possible strategies.

### Strategy A — Module Contracts

```text
Reporting
 ├── Sales.Contracts
 ├── Inventory.Contracts
 └── Purchasing.Contracts
```

### Strategy B — Reporting Read Model

For complex reporting:

```text
Business Modules
      ↓
Reporting Data Pipeline
      ↓
Reporting Read Model
      ↓
Reporting
```

The architecture should start with Strategy A.

A dedicated reporting read model can be introduced later when necessary.

---

# 54. Optional Module Rule

An optional module must satisfy:

```text
Core Platform
    +
Core Modules
```

without requiring:

```text
Optional Module
```

Example:

```text
Accounting
```

may depend on:

```text
Sales.Contracts
Purchasing.Contracts
Payments.Contracts
```

but:

```text
Sales
```

must not depend on:

```text
Accounting
```

This keeps Accounting optional.

---

# 55. Accounting Example

Allowed:

```text
Accounting
 ├── Sales.Contracts
 ├── Purchasing.Contracts
 └── Payments.Contracts
```

Forbidden:

```text
Sales
 ↓
Accounting
```

If Sales eventually needs accounting integration, the dependency should be inverted through a platform event/contract.

---

# 56. Domain Events

Cross-module side effects should eventually use domain/application events where appropriate.

Example:

```text
SaleCompleted
```

can be consumed by:

```text
Inventory
CashManagement
Audit
Reporting
Accounting
```

Conceptually:

```text
                 SaleCompleted
                       │
          ┌────────────┼────────────┐
          ▼            ▼            ▼
      Inventory       Audit     CashManagement
                                    │
                                    ▼
                              Accounting
```

This is particularly important for optional modules.

---

# 57. Why Events Matter

Without events:

```text
Sales
 ↓
Accounting
 ↓
Loyalty
 ↓
Reporting
 ↓
Audit
```

Sales eventually becomes coupled to everything.

With events:

```text
Sales
 ↓
SaleCompleted
 ↓
Subscribers
```

Sales remains independent.

---

# 58. Event Rule

Events should communicate facts.

Good:

```text
SaleCompleted
StockAdjusted
PurchaseReceived
PaymentCompleted
CustomerCreated
```

Bad:

```text
DoAccountingForSale
UpdateInventoryNow
SendThisToPOS
```

The first describes **what happened**.

The second describes **what another module should do**.

---

# 59. Module Dependency Layers

The platform should have three conceptual levels.

```text
LEVEL 1
Foundation
│
└── Platform


LEVEL 2
Core Business
│
├── Catalog
├── Inventory
├── Sales
├── Purchasing
├── Customers
├── Suppliers
├── Pricing
└── Payments


LEVEL 3
Experience / Support
│
├── POS
├── Reporting
├── Users
├── Audit
└── CashManagement


LEVEL 4
Optional
│
├── Accounting
├── Loyalty
├── Manufacturing
├── Restaurant
├── MultiBranch
└── etc.
```

---

# 60. MVP Dependency Set

The first architecture proof should use only:

```text
Platform
   │
Catalog
   │
Inventory
   │
Sales
   │
Payments
   │
POS
```

Plus:

```text
Users
Audit
```

as supporting infrastructure where necessary.

Everything else should remain outside the first vertical slice.

---

# 61. First Vertical Slice

The first working workflow should be:

```text
1. Create Product
       ↓
2. Add Stock
       ↓
3. Open POS
       ↓
4. Find Product
       ↓
5. Add Product to Cart
       ↓
6. Calculate Price
       ↓
7. Complete Sale
       ↓
8. Process Payment
       ↓
9. Reduce Stock
       ↓
10. Record Audit
```

This proves the most important boundaries.

---

# 62. What This Vertical Slice Must NOT Prove Yet

We do not need:

```text
Accounting
Manufacturing
Restaurant
MultiBranch
Synchronization
Cloud Backup
Advanced Reporting
```

before the core architecture works.

---

# 63. Final Module Graph

The final intended relationship is:

```text
                              PLATFORM
                                  │
                                  ▼
                              CATALOG
                             /       \
                            /         \
                           ▼           ▼
                     INVENTORY       PRICING
                        │              │
                        │              │
                        ▼              ▼
                     ┌──────── SALES ────────┐
                     │            │          │
                     │            │          │
                     ▼            ▼          ▼
                PAYMENTS     INVENTORY    CUSTOMERS
                     │
                     ▼
                    POS


PURCHASING
    │
    ├────────► CATALOG
    ├────────► SUPPLIERS
    └────────► INVENTORY


REPORTING
    ├────────► SALES CONTRACTS
    ├────────► INVENTORY CONTRACTS
    └────────► PURCHASING CONTRACTS


AUDIT
    ▲
    │
    ├── Sales
    ├── Inventory
    ├── Purchasing
    └── Users


OPTIONAL:

ACCOUNTING
    ├────────► Sales Contracts
    ├────────► Purchasing Contracts
    └────────► Payments Contracts

LOYALTY
    ├────────► Sales Contracts
    └────────► Customers Contracts
```

---

# 64. Forbidden Dependencies

The following dependencies are explicitly forbidden:

```text
Platform → Catalog
Platform → Inventory
Platform → Sales

Catalog → Inventory
Catalog → Sales
Catalog → POS

Inventory → POS
Inventory → Sales UI

Sales → POS
Sales → POS.UI

Domain → EF Core
Domain → SQLite
Domain → WPF
Domain → ASP.NET

Module A → Module B.Infrastructure
Module A → Module B.UI

UI → DbContext
UI → Repository

Business Logic → HttpClient
```

---

# 65. Dependency Test Rules

These rules should eventually become automated architecture tests.

```text
ARCH-001
Platform cannot reference business modules.

ARCH-002
Domain cannot reference Infrastructure.

ARCH-003
Domain cannot reference UI.

ARCH-004
Application cannot reference UI.

ARCH-005
Module cannot reference another module's Infrastructure.

ARCH-006
Module cannot reference another module's UI.

ARCH-007
Cross-module dependencies must use Contracts.

ARCH-008
UI cannot directly reference DbContext.

ARCH-009
Business modules cannot require HTTP.

ARCH-010
Circular module dependencies are forbidden.
```

---

# 66. Boundary Test

Whenever a new class is introduced, ask:

```text
Who owns this concept?
```

Then:

```text
Who needs to know about it?
```

Then:

```text
Can they depend on an interface/contract instead of the implementation?
```

Then:

```text
Does this create a new dependency?
```

If yes:

```text
Is that dependency architecturally justified?
```

This process should happen before adding the reference.

---

# 67. Final Decisions

The following decisions are now locked:

### Product ownership

```text
Catalog
```

### Stock ownership

```text
Inventory
```

### Sales ownership

```text
Sales
```

### Cashier workflow

```text
POS
```

### Supplier procurement

```text
Purchasing
```

### Customer information

```text
Customers
```

### Supplier information

```text
Suppliers
```

### Price calculation

```text
Pricing
```

### Payment processing

```text
Payments
```

### Reports

```text
Reporting
```

### User identity/permissions

```text
Users
```

### Historical system actions

```text
Audit
```

### Physical cash operations

```text
CashManagement
```

### Accounting

```text
Optional Accounting Module
```

---

# 68. Architectural Principle for the Next Steps

The platform should evolve like this:

```text
Platform
   ↓
Contracts
   ↓
Modules
   ↓
Business Workflows
   ↓
UI
```

not:

```text
UI
 ↓
Database
 ↓
Everything else
```

The database is an implementation detail of the business modules, not the architecture around which the application is designed.

---

# 69. Step Completion Criteria

This architecture step is complete when:

* Every initial module has a clear responsibility.
* Every module has a defined owner for its data.
* Cross-module dependencies are explicitly defined.
* Forbidden dependencies are documented.
* Contracts are the only public module boundary.
* Optional modules cannot contaminate the core.
* The first vertical slice is identified.
* The module dependency graph is stable enough to create the solution.

The next step is therefore:

> **Create the actual .NET solution/project skeleton and enforce these dependency rules in the project references.**

No business entities yet.

The first implementation should create the projects, references, folders, module contracts, DI composition root, and architecture tests—then prove that the solution compiles while still containing essentially **zero business logic**.
