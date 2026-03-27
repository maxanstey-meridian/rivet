# rivet/php-reflector

[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![PHP](https://img.shields.io/badge/php-%3E%3D8.1-8892BF)](composer.json)

PHP reflection utilities for [Rivet](https://github.com/maxanstey-meridian/rivet) contract generation. Extracts type information from your DTOs and controller methods, emitting a JSON contract that Rivet consumes to generate TypeScript types and clients.

## Install

```bash
composer require rivet/php-reflector --dev
```

## Quick start

1. Annotate your controller methods:

```php
use Rivet\PhpReflector\Attribute\RivetRoute;
use Rivet\PhpReflector\Attribute\RivetResponse;

class ProductController
{
    #[RivetRoute('GET', '/products/{id}')]
    #[RivetResponse(ProductDto::class)]
    public function show(int $id): void {}

    #[RivetRoute('POST', '/products')]
    #[RivetResponse(ProductDto::class)]
    public function store(CreateProductDto $payload): void {}
}
```

2. Run the CLI:

```bash
vendor/bin/rivet-reflect --dir src/Dto --out rivet-contract.json
```

3. Feed the JSON to `dotnet-rivet` or use it directly.

## DTO conventions

Public properties on your DTO classes are reflected automatically. Use PHP native types and `@var` docblocks to provide full type information.

### Scalars

```php
class ProductDto
{
    public string $title;
    public int $id;
    public float $price;
    public bool $active;
}
```

Maps to: `string`, `number` (int32/double), `boolean`.

### Nullable

```php
public ?string $description;
```

Maps to a nullable wrapper around the inner type.

### Backed enums

```php
enum ProductStatus: string
{
    case Active = 'active';
    case Draft = 'draft';
    case Archived = 'archived';
}

// In your DTO:
public ProductStatus $status;
```

String-backed and int-backed enums are both supported. The enum values are emitted in the contract.

### Arrays with `@var`

Untyped `array` properties require a `@var` annotation. Without one, the reflector emits a diagnostic warning and maps the type to `unknown`.

```php
/** @var list<string> */
public array $tags;

/** @var array<string, int> */
public array $metadata;
```

- `list<T>` maps to an array of `T`
- `array<string, T>` maps to a dictionary (Record) with string keys

### Nested DTOs

```php
public UserDto $author;
```

Referenced classes are walked recursively. Circular references are handled via a visited set.

### Inline object shapes

```php
/** @var array{width: int, height: int} */
public array $dimensions;
```

Maps to an inline object type with named properties.

### String and int unions

```php
/** @var 'small'|'medium'|'large' */
public string $size;

/** @var 1|2|3 */
public int $rating;
```

Maps to string union and int union types respectively.

## Attributes

### `#[RivetRoute(method, route)]`

Marks a controller method as an API endpoint. Applied to public methods.

```php
#[RivetRoute('GET', '/products/{id}')]
public function show(int $id): void {}

#[RivetRoute('DELETE', '/products/{id}')]
public function destroy(int $id): void {}
```

- `method`: HTTP method (`GET`, `POST`, `PUT`, `DELETE`, etc.)
- `route`: URI template with `{param}` placeholders

Method parameters are classified automatically:
- Parameters matching `{placeholders}` in the route are **route params**
- Class-typed parameters are **body params**
- All others are **query params**

### `#[RivetResponse(type)]`

Declares the response type for an endpoint. Can be a class reference or an inline type string.

```php
#[RivetRoute('GET', '/products/{id}')]
#[RivetResponse(ProductDto::class)]
public function show(int $id): void {}

#[RivetRoute('GET', '/products')]
#[RivetResponse('list<ProductDto>')]
public function index(string $status, int $page): void {}
```

Methods with `#[RivetRoute]` but no `#[RivetResponse]` emit a diagnostic warning and produce an endpoint with no response type.

## CLI flags

```
vendor/bin/rivet-reflect --dir <path> --out <file>
```

| Flag | Description |
|------|-------------|
| `--dir` | Directory containing PHP source files to reflect |
| `--out` | Output file path for the JSON contract |

The CLI scans the directory recursively, loads all PHP files, and reflects public classes (excluding enums and interfaces). Diagnostics (warnings and errors) are printed to stderr.

Exit codes:
- `0` — success
- `1` — reflection errors or missing directory

## Build integration

### Composer script

```json
{
    "scripts": {
        "rivet": "rivet-reflect --dir src/Dto --out rivet-contract.json"
    }
}
```

```bash
composer rivet
```

### Makefile

```makefile
rivet-contract.json: $(shell find src/Dto -name '*.php')
	vendor/bin/rivet-reflect --dir src/Dto --out $@
```

### CI (GitHub Actions)

```yaml
- name: Generate Rivet contract
  run: vendor/bin/rivet-reflect --dir src/Dto --out rivet-contract.json

- name: Verify contract is up to date
  run: git diff --exit-code rivet-contract.json
```

## Framework adapters

### Laravel

Register the command in your `AppServiceProvider` or via package auto-discovery:

```php
use Rivet\PhpReflector\Laravel\RivetReflectCommand;

// In a service provider:
$this->commands([RivetReflectCommand::class]);
```

```bash
php artisan rivet:reflect --out rivet-contract.json
```

The Laravel adapter reads routes from `Route::getRoutes()` automatically — no `--dir` flag needed.

Requires `illuminate/routing` (`^10.0|^11.0|^12.0`).

### Symfony

Register the command as a service:

```yaml
services:
    Rivet\PhpReflector\Symfony\RivetReflectCommand:
        arguments:
            - '@router'
        tags: ['console.command']
```

```bash
php bin/console rivet:reflect --out rivet-contract.json
```

Requires `symfony/routing` (`^6.0|^7.0`) and `symfony/console` (`^6.0|^7.0`).

## License

[MIT](LICENSE)
