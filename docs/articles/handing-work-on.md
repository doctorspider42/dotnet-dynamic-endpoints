# Handing work on: validators → processor

A validator that already parsed a value, or loaded an entity, hands it on instead of letting the processor repeat the work:

```csharp
// in a parameter validator
var document = Decode(context.GetValue<string>());
context.SetParsedValue(document);               // for the validated parameter (or pass a parameter name)
context.Items["customer"] = customer;           // anything else, shared by filters, validators and the processor

// in the processor
var document = request.GetParsedValue<Document>("document");
var customer = (Customer)request.Items["customer"]!;
```
