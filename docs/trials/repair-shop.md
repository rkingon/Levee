# Repair Shop

A service for independent auto repair shops: their customers, vehicles and work orders, from the counter to the invoice.
Many shops use one deployment. Build it new, in .NET 10 on Postgres.

## Who uses it

- **Staff** of a shop: the **owner**, **mechanics** and the **front desk**. Each belongs to one shop.
- **The platform operator**, who runs the deployment and sets shops up.
- **A shop's own integrations**, calling with an API key issued to that shop.
- **An AI assistant** the staff talk to ("what's waiting on parts?", "open an order for the blue Civic").
- **A shop's customers**, who check on their car without an account.

## What it does

### Shops and staff
- The operator creates a shop and its owner.
- The owner invites staff by email and sets each one's role: owner, mechanic or front desk.
- The owner issues API keys for the shop's integrations. A key is shown once, when it's issued, and can be revoked.
- A shop never sees another shop's data, whoever asks and however.

### Customers and vehicles
- A customer has a name, a phone number and an email. These are personal: they mustn't end up anywhere they don't need
  to be, logs and records of who-changed-what included.
- A vehicle has a VIN (17 characters, no I, O or Q), a plate, and a year, make and model. It belongs to a customer.

### Work orders
- The front desk opens a work order for a vehicle, with the customer's complaint.
- Its states: open, in progress (assigned to a mechanic), awaiting parts, ready, closed. It can be cancelled before
  it's closed.
- Lines: labor (hours at the shop's rate) and parts (part number, quantity, price). The order keeps its total.
- Two people editing the same order mustn't silently overwrite each other: the second one is told it changed.
- Every change records who made it and when.

### Parts pricing
- A mechanic looks up a part's price from **PartsHub**, the shop's supplier, over its HTTP API. PartsHub charges per
  lookup.
- Every lookup is recorded for billing reconciliation, even if the change it was for fails or is abandoned.
- In development and tests nothing calls PartsHub: a stand-in answers instead.

### Notifications
- When an order is ready, the customer gets a text message.
- Not every shop has a texting provider set up. A shop without one simply doesn't send texts; nothing else changes.

### The board
- The front desk keeps a screen of the shop's open orders, which updates as orders change, without refreshing.

### Invoicing
- Every night, closed orders are invoiced. A shop can have thousands, and the run can take a while.
- If the run is interrupted, it picks up where it stopped. An order is never invoiced twice.

### How it's reached
- An HTTP API for the shop's own web app.
- The AI assistant reaches the same capabilities over MCP.
- A public page for customers: `GET /status/{token}` shows their order's state, from a link in their text. No login;
  the token is all they have.
- An operator command line: create the database, apply migrations, set up the first operator.

## Done means
- It builds, its tests pass, and it runs locally against Postgres (docker compose is fine).
- Tests show the things above that matter most: shops isolated from each other, the overwrite case, a lookup recorded
  when its change fails, invoicing that resumes without invoicing twice, and the public status page.
- A short README: how to run it, and anything you couldn't do or decided differently, with why.
