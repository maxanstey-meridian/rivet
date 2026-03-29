<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

use Rivet\PhpReflector\Attribute\RivetRoute;

class ReflectCommand
{
    /** @var resource */
    private $stderr;

    /**
     * @param resource|null $stderr Stream for diagnostic output (defaults to STDERR)
     */
    public function __construct($stderr = null)
    {
        $this->stderr = $stderr ?? STDERR;
    }

    public function run(string $dir, string $out): int
    {
        if (!is_dir($dir)) {
            fwrite($this->stderr, "Error: directory does not exist: {$dir}\n");
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

        $classes = TypeCollector::collect(...$fqcns);

        $controllers = array_filter($fqcns, function (string $fqcn) {
            if (!class_exists($fqcn)) {
                return false;
            }
            $ref = new \ReflectionClass($fqcn);
            foreach ($ref->getMethods(\ReflectionMethod::IS_PUBLIC) as $method) {
                if ($method->getAttributes(RivetRoute::class) !== []) {
                    return true;
                }
            }
            return false;
        });

        if ($controllers !== []) {
            try {
                $contract = ControllerWalker::walk(array_values($controllers), array_values($classes));
            } catch (\RuntimeException $e) {
                fwrite($this->stderr, $e->getMessage() . "\n");
                return 1;
            }
        } else {
            $contract = PropertyWalker::walk(...array_values($classes));
        }

        /** @var Diagnostics $diagnostics */
        $diagnostics = $contract['diagnostics'];
        foreach ($diagnostics->formatMessages() as $line) {
            fwrite($this->stderr, $line . "\n");
        }

        if ($diagnostics->hasErrors()) {
            return 1;
        }

        $json = ContractEmitter::emit($contract);
        file_put_contents($out, $json . "\n");

        return 0;
    }
}
