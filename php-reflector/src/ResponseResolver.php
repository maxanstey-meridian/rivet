<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

use Rivet\PhpReflector\Attribute\RivetResponse;

class ResponseResolver
{
    public static function resolve(\ReflectionMethod|\ReflectionClass $target): ?array
    {
        $attrs = $target->getAttributes(RivetResponse::class);

        if ($attrs === []) {
            return null;
        }

        $type = $attrs[0]->newInstance()->type;

        if (class_exists($type)) {
            return ['kind' => 'ref', 'name' => (new \ReflectionClass($type))->getShortName()];
        }

        if (preg_match('/^[A-Z][A-Za-z0-9_\\\\]*$/', $type)) {
            throw new \InvalidArgumentException("RivetResponse class not found: $type");
        }

        return TypeParser::parse($type);
    }
}
