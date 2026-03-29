<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

use Rivet\PhpReflector\Attribute\RivetResponse;
use Rivet\PhpReflector\Attribute\RivetRoute;

class SampleController
{
    #[RivetRoute('GET', '/orders/{id}')]
    #[RivetResponse(OrderDto::class)]
    public function show(int $id): void {}

    #[RivetRoute('POST', '/orders')]
    #[RivetResponse(OrderDto::class)]
    public function store(PersonDto $payload): void {}

    #[RivetRoute('GET', '/orders')]
    #[RivetResponse(OrderDto::class)]
    public function index(string $status, int $page): void {}

    #[RivetRoute('DELETE', '/orders/{id}')]
    public function destroy(int $id): void {}

    #[RivetRoute('PUT', '/orders/{id}')]
    #[RivetResponse(OrderDto::class)]
    public function update(int $id, PersonDto $payload): void {}

    public function health(): void {}
}
