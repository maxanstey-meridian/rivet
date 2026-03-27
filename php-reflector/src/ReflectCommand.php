<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

class ReflectCommand
{
    public function run(string $dir, string $out): int
    {
        if (!is_dir($dir)) {
            fwrite(STDERR, "Error: directory does not exist: {$dir}\n");
            return 1;
        }

        $fqcns = ClassFinder::find($dir);

        // Require source files so reflection works on non-autoloaded dirs
        $iterator = new \RecursiveIteratorIterator(
            new \RecursiveDirectoryIterator($dir, \FilesystemIterator::SKIP_DOTS)
        );
        foreach ($iterator as $file) {
            if ($file->getExtension() === 'php') {
                require_once $file->getPathname();
            }
        }

        // Filter to walkable classes (not enums, not interfaces)
        $classes = array_filter($fqcns, fn(string $fqcn) =>
            class_exists($fqcn) && !enum_exists($fqcn) && !interface_exists($fqcn)
        );

        $contract = PropertyWalker::walk(...array_values($classes));
        $json = ContractEmitter::emit($contract);
        file_put_contents($out, $json . "\n");

        return 0;
    }
}
