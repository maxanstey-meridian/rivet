using Rivet;

// The document's security lives in source, beside the contracts that name it: the bearer
// scheme every .Secure("bearer") points at, and bearer as the default each endpoint without
// security of its own inherits. Generation and --check read the same thing, so neither needs
// --security, and a default can't live in one CI script while the other run never sees it.
[assembly: RivetSecurityScheme("bearer", "http", scheme: "bearer")]
[assembly: RivetGlobalSecurity(0)]
[assembly: RivetGlobalSecurityScheme(0, "bearer", new string[0])]
