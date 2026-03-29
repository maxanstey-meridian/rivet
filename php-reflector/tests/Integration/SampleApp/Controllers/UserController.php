<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Integration\SampleApp\Controllers;

use Rivet\PhpReflector\Attribute\RivetResponse;
use Rivet\PhpReflector\Attribute\RivetRoute;
use Rivet\PhpReflector\Tests\Integration\SampleApp\Dtos\UserDto;

class UserController
{
    #[RivetRoute('GET', '/users/{id}')]
    #[RivetResponse(UserDto::class)]
    public function show(int $id): void {}
}
