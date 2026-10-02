# Localized error messages

English and Polish are built in, with proper plural forms ("2 znaki", "5 znaków").

```csharp
builder.Services.AddDynamicEndpoints(o =>
{
    o.Messages.DefaultCulture = "pl";                                    // always Polish…
    o.Messages.UseRequestCulture = true;                                 // …or per request (app.UseRequestLocalization())
    o.Messages.Set("pl", "required.header", "Brak nagłówka {0}.");       // override a single text
    o.Messages.Set("de", "minLength", "Mindestens {0} Zeichen.");        // add a language
    o.Messages.Localizer = c => localizer[c.Key, c.Arguments.ToArray()]; // or route everything through IStringLocalizer
});
```

Keys follow the error codes (`minLength`, `format.email`, `required.query`, `type.integer`, `file.maxSize`, …), and
`DynamicValidationMessages.Keys` lists them all. Rule messages are whatever the admin wrote.
