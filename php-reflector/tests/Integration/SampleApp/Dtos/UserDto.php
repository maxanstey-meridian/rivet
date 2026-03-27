<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Integration\SampleApp\Dtos;

class UserDto
{
    public string $name;
    public ?string $email;
    public AddressDto $address;
}
