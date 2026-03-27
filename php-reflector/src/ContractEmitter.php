<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

class ContractEmitter
{
    public static function emit(array $contract): string
    {
        $cleaned = self::stripNulls($contract);
        return json_encode($cleaned, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
    }

    private static function stripNulls(array $data): array
    {
        $isList = array_is_list($data);
        $result = [];
        foreach ($data as $key => $value) {
            if ($value === null) {
                continue;
            }
            $result[$key] = is_array($value) ? self::stripNulls($value) : $value;
        }
        return $isList ? array_values($result) : $result;
    }
}
