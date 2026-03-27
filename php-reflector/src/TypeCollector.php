<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

use Rivet\PhpReflector\Attribute\RivetType;

class TypeCollector
{
    /**
     * @return list<string> FQCNs annotated with #[RivetType]
     */
    public static function collect(string ...$fqcns): array
    {
        $result = [];

        foreach ($fqcns as $fqcn) {
            try {
                $ref = new \ReflectionClass($fqcn);
            } catch (\ReflectionException) {
                continue;
            }

            if ($ref->isEnum() || $ref->isInterface()) {
                continue;
            }

            if ($ref->getAttributes(RivetType::class) !== []) {
                $result[] = $fqcn;
            }
        }

        return $result;
    }
}
