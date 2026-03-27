<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Laravel;

use Illuminate\Console\Command;
use Illuminate\Support\Facades\Route;
use Rivet\PhpReflector\ClassFinder;
use Rivet\PhpReflector\ContractEmitter;
use Rivet\PhpReflector\Diagnostics;
use Rivet\PhpReflector\LaravelRouteWalker;
use Rivet\PhpReflector\TypeCollector;

class RivetReflectCommand extends Command
{
    protected $signature = 'rivet:reflect {--out= : Output file path} {--dir= : Directory to scan for #[RivetType] classes}';
    protected $description = 'Generate Rivet contract JSON from Laravel routes';

    public function handle(): int
    {
        $routes = LaravelRouteWalker::fromRouteCollection(Route::getRoutes());

        $dir = $this->option('dir') ?? app_path();
        $extraFqcns = [];
        if (is_dir($dir)) {
            $allFqcns = ClassFinder::find($dir);

            // Require files so reflection works on non-autoloaded dirs
            $iterator = new \RecursiveIteratorIterator(
                new \RecursiveDirectoryIterator($dir, \FilesystemIterator::SKIP_DOTS)
            );
            foreach ($iterator as $file) {
                if ($file->getExtension() === 'php') {
                    require_once $file->getPathname();
                }
            }

            $extraFqcns = TypeCollector::collect(...$allFqcns);
        }

        $contract = LaravelRouteWalker::walk($routes, $extraFqcns);

        /** @var Diagnostics $diagnostics */
        $diagnostics = $contract['diagnostics'];
        foreach ($diagnostics->formatMessages() as $line) {
            $this->warn($line);
        }

        if ($diagnostics->hasErrors()) {
            return self::FAILURE;
        }

        $json = ContractEmitter::emit($contract);

        $out = $this->option('out');
        if ($out !== null) {
            file_put_contents($out, $json);
            $this->info("Contract written to $out");
        } else {
            $this->line($json);
        }

        return self::SUCCESS;
    }
}
