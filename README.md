# Payments
Handles payments, and access to digital goods and services

## Configuration
Configuration is handled via [asp.net configuration providers](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-6.0#environment-variables)
Keys with defaults are set in [`appsettings.json`](appsettings.json)

### How purchases work
1. Start top up 
2. wait till payment is verified
3. (optional) plan purchase (locks some amount of balance)
4. purchase product/service 

## Setup
Becuase it is the esiest by default stripe is used. 
To configure stripe get your stripe `KEY` and `SIGNING_SECRET` from stripe.com and set them as configuration. 
(either modify appsettings.json or set the enviromentvariables `STRIPE__KEY` and `STRIPE__SIGNING_SECRET`)  
Next create a webhook callback to `/Callback/stripe` that triggers on confirmed purchase.

#### Paypal
Paypal can be configured with `PAYPAL__SECRET`, `PAYPAL__ID` and `PAYPAL__IS_SANDBOX` 
Create a webhook callback to `/Callback/paypal` to allow for payments to be verified.

## Events 
This microservice can produce transaction events into a Kafka Topic.
To configure it set the configuration variables `KAFKA_HOST` and `KAFKA_TRANSACTION_TOPIC`.
The format and fields of the events can be seen in the [TransactionEvent class](Models/TransactionEvent.cs) 
