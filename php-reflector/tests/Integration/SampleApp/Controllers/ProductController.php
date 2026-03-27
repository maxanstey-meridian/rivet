<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Integration\SampleApp\Controllers;

use Rivet\PhpReflector\Attribute\RivetResponse;
use Rivet\PhpReflector\Attribute\RivetRoute;
use Rivet\PhpReflector\Tests\Integration\SampleApp\Dtos\ProductDto;
use Rivet\PhpReflector\Tests\Integration\SampleApp\Dtos\ProductFilterDto;

class ProductController
{
    #[RivetRoute('GET', '/products/{id}')]
    #[RivetResponse(ProductDto::class)]
    public function show(int $id): void {}

    #[RivetRoute('POST', '/products')]
    #[RivetResponse(ProductDto::class)]
    public function store(ProductFilterDto $payload): void {}

    #[RivetRoute('GET', '/products')]
    #[RivetResponse('list<ProductDto>')]
    public function index(string $status, int $page): void {}

    #[RivetRoute('DELETE', '/products/{id}')]
    public function destroy(int $id): void {}
}
