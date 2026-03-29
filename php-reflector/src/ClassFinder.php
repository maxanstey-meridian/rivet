<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

class ClassFinder
{
    /**
     * @return list<string> FQCNs of classes and enums found in PHP files
     */
    public static function find(string $directory): array
    {
        if (!is_dir($directory)) {
            throw new \InvalidArgumentException("Directory does not exist: {$directory}");
        }

        $fqcns = [];
        $iterator = new \RecursiveIteratorIterator(
            new \RecursiveDirectoryIterator($directory, \FilesystemIterator::SKIP_DOTS)
        );

        foreach ($iterator as $file) {
            if ($file->getExtension() !== 'php') {
                continue;
            }

            $fqcn = self::extractFqcn($file->getPathname());
            if ($fqcn !== null) {
                $fqcns[] = $fqcn;
            }
        }

        sort($fqcns);
        return $fqcns;
    }

    private static function extractFqcn(string $filePath): ?string
    {
        $contents = file_get_contents($filePath);
        if ($contents === false) {
            return null;
        }

        $tokens = token_get_all($contents);
        $namespace = '';
        $className = null;
        $count = count($tokens);

        for ($i = 0; $i < $count; $i++) {
            if (!is_array($tokens[$i])) {
                continue;
            }

            if ($tokens[$i][0] === T_NAMESPACE) {
                $namespace = self::extractName($tokens, $i + 1, $count);
            }

            if ($tokens[$i][0] === T_CLASS || $tokens[$i][0] === T_ENUM) {
                $className = self::extractSimpleName($tokens, $i + 1, $count);
                break;
            }
        }

        if ($className === null) {
            return null;
        }

        return $namespace !== '' ? $namespace . '\\' . $className : $className;
    }

    private static function extractName(array $tokens, int $start, int $count): string
    {
        $name = '';
        for ($i = $start; $i < $count; $i++) {
            if (is_array($tokens[$i])) {
                if ($tokens[$i][0] === T_NAME_QUALIFIED || $tokens[$i][0] === T_STRING) {
                    $name .= $tokens[$i][1];
                } elseif ($tokens[$i][0] === T_WHITESPACE) {
                    continue;
                } else {
                    break;
                }
            } elseif ($tokens[$i] === ';' || $tokens[$i] === '{') {
                break;
            }
        }
        return $name;
    }

    private static function extractSimpleName(array $tokens, int $start, int $count): ?string
    {
        for ($i = $start; $i < $count; $i++) {
            if (is_array($tokens[$i]) && $tokens[$i][0] === T_STRING) {
                return $tokens[$i][1];
            }
        }
        return null;
    }
}
