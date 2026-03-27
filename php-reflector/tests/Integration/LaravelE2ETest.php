<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Integration;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ContractEmitter;
use Rivet\PhpReflector\LaravelRouteWalker;
use Rivet\PhpReflector\Tests\Integration\SampleApp\Controllers\ProductController;
use Rivet\PhpReflector\Tests\Integration\SampleApp\Controllers\UserController;

class LaravelE2ETest extends TestCase
{
    private static array $result;

    public static function setUpBeforeClass(): void
    {
        $routes = [
            ['httpMethod' => 'GET', 'uri' => '/products/{id}', 'controller' => ProductController::class, 'action' => 'show'],
            ['httpMethod' => 'POST', 'uri' => '/products', 'controller' => ProductController::class, 'action' => 'store'],
            ['httpMethod' => 'GET', 'uri' => '/products', 'controller' => ProductController::class, 'action' => 'index'],
            ['httpMethod' => 'DELETE', 'uri' => '/products/{id}', 'controller' => ProductController::class, 'action' => 'destroy'],
            ['httpMethod' => 'GET', 'uri' => '/products/paginated', 'controller' => ProductController::class, 'action' => 'paginated'],
            ['httpMethod' => 'GET', 'uri' => '/users/{id}', 'controller' => UserController::class, 'action' => 'show'],
        ];

        self::$result = LaravelRouteWalker::walk($routes);
    }

    public function testFirstEndpointReflects(): void
    {
        $endpoints = self::$result['endpoints'];

        $this->assertCount(6, $endpoints);
        $this->assertSame('show', $endpoints[0]['name']);
        $this->assertSame('product', $endpoints[0]['controllerName']);
    }

    public function testShowParams(): void
    {
        $ep = $this->findEndpoint('show');

        $this->assertCount(1, $ep['params']);
        $this->assertSame('id', $ep['params'][0]['name']);
        $this->assertSame('route', $ep['params'][0]['source']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'], $ep['params'][0]['type']);
    }

    public function testStoreBodyParam(): void
    {
        $ep = $this->findEndpoint('store');

        $this->assertCount(1, $ep['params']);
        $this->assertSame('payload', $ep['params'][0]['name']);
        $this->assertSame('body', $ep['params'][0]['source']);
        $this->assertSame('ref', $ep['params'][0]['type']['kind']);
        $this->assertSame('ProductFilterDto', $ep['params'][0]['type']['name']);
    }

    public function testIndexQueryParams(): void
    {
        $ep = $this->findEndpoint('index');

        $this->assertCount(3, $ep['params']);
        $this->assertSame('status', $ep['params'][0]['name']);
        $this->assertSame('query', $ep['params'][0]['source']);
        $this->assertSame('page', $ep['params'][1]['name']);
        $this->assertSame('query', $ep['params'][1]['source']);
    }

    public function testNullableQueryParamEmitsNullableType(): void
    {
        $ep = $this->findEndpoint('index');

        $search = null;
        foreach ($ep['params'] as $p) {
            if ($p['name'] === 'search') {
                $search = $p;
                break;
            }
        }
        $this->assertNotNull($search, 'Param "search" not found');
        $this->assertSame('query', $search['source']);
        $this->assertSame('nullable', $search['type']['kind']);
        $this->assertSame('string', $search['type']['inner']['type']);
    }

    public function testIndexResponseIsArray(): void
    {
        $ep = $this->findEndpoint('index');

        $this->assertSame('array', $ep['returnType']['kind']);
        $this->assertSame('ref', $ep['returnType']['element']['kind']);
        $this->assertSame('ProductDto', $ep['returnType']['element']['name']);
    }

    public function testDestroyHasNoResponse(): void
    {
        $ep = $this->findEndpoint('destroy');

        $this->assertNull($ep['returnType']);
        $this->assertSame([], $ep['responses']);
    }

    public function testProductDtoScalars(): void
    {
        $product = $this->findType('ProductDto');

        $title = $this->findProp($product, 'title');
        $this->assertSame('primitive', $title['type']['kind']);
        $this->assertSame('string', $title['type']['type']);

        $id = $this->findProp($product, 'id');
        $this->assertSame('primitive', $id['type']['kind']);
        $this->assertSame('number', $id['type']['type']);
        $this->assertSame('int32', $id['type']['format']);

        $price = $this->findProp($product, 'price');
        $this->assertSame('primitive', $price['type']['kind']);
        $this->assertSame('number', $price['type']['type']);
        $this->assertSame('double', $price['type']['format']);

        $active = $this->findProp($product, 'active');
        $this->assertSame('primitive', $active['type']['kind']);
        $this->assertSame('boolean', $active['type']['type']);
    }

    public function testProductDtoNullable(): void
    {
        $product = $this->findType('ProductDto');
        $desc = $this->findProp($product, 'description');

        $this->assertSame('nullable', $desc['type']['kind']);
        $this->assertSame('string', $desc['type']['inner']['type']);
    }

    public function testProductDtoEnumRefs(): void
    {
        $product = $this->findType('ProductDto');

        $status = $this->findProp($product, 'status');
        $this->assertSame('ref', $status['type']['kind']);
        $this->assertSame('ProductStatus', $status['type']['name']);

        $priority = $this->findProp($product, 'priority');
        $this->assertSame('ref', $priority['type']['kind']);
        $this->assertSame('Priority', $priority['type']['name']);
    }

    public function testProductDtoNestedRef(): void
    {
        $product = $this->findType('ProductDto');
        $author = $this->findProp($product, 'author');

        $this->assertSame('ref', $author['type']['kind']);
        $this->assertSame('UserDto', $author['type']['name']);
    }

    public function testProductDtoArray(): void
    {
        $product = $this->findType('ProductDto');
        $tags = $this->findProp($product, 'tags');

        $this->assertSame('array', $tags['type']['kind']);
        $this->assertSame('string', $tags['type']['element']['type']);
    }

    public function testProductDtoDictionary(): void
    {
        $product = $this->findType('ProductDto');
        $meta = $this->findProp($product, 'metadata');

        $this->assertSame('dictionary', $meta['type']['kind']);
        $this->assertSame('number', $meta['type']['value']['type']);
        $this->assertSame('int32', $meta['type']['value']['format']);
    }

    public function testProductDtoInlineObject(): void
    {
        $product = $this->findType('ProductDto');
        $dims = $this->findProp($product, 'dimensions');

        $this->assertSame('inlineObject', $dims['type']['kind']);
        $this->assertCount(2, $dims['type']['properties']);

        $propNames = array_column($dims['type']['properties'], 'name');
        $this->assertContains('width', $propNames);
        $this->assertContains('height', $propNames);
    }

    public function testProductDtoStringUnion(): void
    {
        $product = $this->findType('ProductDto');
        $size = $this->findProp($product, 'size');

        $this->assertSame('stringUnion', $size['type']['kind']);
        $this->assertSame(['small', 'medium', 'large'], $size['type']['values']);
    }

    public function testProductDtoIntUnion(): void
    {
        $product = $this->findType('ProductDto');
        $rating = $this->findProp($product, 'rating');

        $this->assertSame('intUnion', $rating['type']['kind']);
        $this->assertSame([1, 2, 3], $rating['type']['values']);
    }

    public function testSixEndpointsTotal(): void
    {
        $this->assertCount(6, self::$result['endpoints']);
    }

    public function testUserDtoNotDuplicated(): void
    {
        $typeNames = array_column(self::$result['types'], 'name');
        $userDtoCount = count(array_filter($typeNames, fn($n) => $n === 'UserDto'));
        $this->assertSame(1, $userDtoCount);
    }

    public function testAddressDtoTransitivelyDiscovered(): void
    {
        $typeNames = array_column(self::$result['types'], 'name');
        $this->assertContains('AddressDto', $typeNames);
    }

    public function testEnums(): void
    {
        $statusEnum = $this->findEnum('ProductStatus');
        $this->assertArrayHasKey('values', $statusEnum);
        $this->assertSame(['active', 'draft', 'archived'], $statusEnum['values']);

        $priorityEnum = $this->findEnum('Priority');
        $this->assertArrayHasKey('intValues', $priorityEnum);
        $this->assertSame([1, 2, 3], $priorityEnum['intValues']);
    }

    public function testGenericResponseTypeRefsDiscovered(): void
    {
        $ep = $this->findEndpointByRoute('/products/paginated');

        $this->assertSame('generic', $ep['returnType']['kind']);
        $this->assertSame('Collection', $ep['returnType']['name']);
        $this->assertCount(1, $ep['returnType']['typeArgs']);
        $this->assertSame('ref', $ep['returnType']['typeArgs'][0]['kind']);
        $this->assertSame('ProductDto', $ep['returnType']['typeArgs'][0]['name']);

        // ProductDto should still be discovered through the generic wrapper
        $typeNames = array_column(self::$result['types'], 'name');
        $this->assertContains('ProductDto', $typeNames);
    }

    public function testArrayOfEnumPropertyEmitsCorrectly(): void
    {
        $filter = $this->findType('ProductFilterDto');
        $priorities = $this->findProp($filter, 'priorities');

        $this->assertSame('array', $priorities['type']['kind']);
        $this->assertSame('ref', $priorities['type']['element']['kind']);
        $this->assertSame('Priority', $priorities['type']['element']['name']);

        // Priority enum should still be present (not lost by being nested in array)
        $enumNames = array_column(self::$result['enums'], 'name');
        $this->assertContains('Priority', $enumNames);
    }

    public function testUserShowEndpointReflectsCorrectly(): void
    {
        $ep = $this->findEndpointByRoute('/users/{id}');

        $this->assertSame('show', $ep['name']);
        $this->assertSame('GET', $ep['httpMethod']);
        $this->assertSame('user', $ep['controllerName']);

        $this->assertCount(1, $ep['params']);
        $this->assertSame('id', $ep['params'][0]['name']);
        $this->assertSame('route', $ep['params'][0]['source']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'], $ep['params'][0]['type']);

        $this->assertSame('ref', $ep['returnType']['kind']);
        $this->assertSame('UserDto', $ep['returnType']['name']);
    }

    public function testFullContractMatchesGoldenFile(): void
    {
        $json = ContractEmitter::emit(self::$result);

        $goldenPath = __DIR__ . '/SampleApp/golden-contract.json';
        if (getenv('UPDATE_GOLDEN')) {
            file_put_contents($goldenPath, $json);
            $this->markTestSkipped('Golden file updated');
        }

        $this->assertFileExists($goldenPath, 'Golden file missing — run with UPDATE_GOLDEN=1 to generate');
        $this->assertJsonStringEqualsJsonString(file_get_contents($goldenPath), $json);
    }

    // --- helpers ---

    private function findType(string $name): array
    {
        foreach (self::$result['types'] as $type) {
            if ($type['name'] === $name) {
                return $type;
            }
        }
        $this->fail("Type '{$name}' not found");
    }

    private function findProp(array $type, string $name): array
    {
        foreach ($type['properties'] as $prop) {
            if ($prop['name'] === $name) {
                return $prop;
            }
        }
        $this->fail("Property '{$name}' not found in type '{$type['name']}'");
    }

    private function findEndpoint(string $name): array
    {
        foreach (self::$result['endpoints'] as $ep) {
            if ($ep['name'] === $name) {
                return $ep;
            }
        }
        $this->fail("Endpoint '{$name}' not found");
    }

    private function findEndpointByRoute(string $routeTemplate): array
    {
        foreach (self::$result['endpoints'] as $ep) {
            if ($ep['routeTemplate'] === $routeTemplate) {
                return $ep;
            }
        }
        $this->fail("Endpoint with route '{$routeTemplate}' not found");
    }

    private function findEnum(string $name): array
    {
        foreach (self::$result['enums'] as $enum) {
            if ($enum['name'] === $name) {
                return $enum;
            }
        }
        $this->fail("Enum '{$name}' not found");
    }
}
