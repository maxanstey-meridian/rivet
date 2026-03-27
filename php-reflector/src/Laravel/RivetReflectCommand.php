<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Laravel;

use Illuminate\Console\Command;
use Illuminate\Support\Facades\Route;
use Rivet\PhpReflector\ContractEmitter;
use Rivet\PhpReflector\LaravelRouteWalker;

class RivetReflectCommand extends Command
{
    protected $signature = 'rivet:reflect {--out= : Output file path}';
    protected $description = 'Generate Rivet contract JSON from Laravel routes';

    public function handle(): int
    {
        $routes = LaravelRouteWalker::fromRouteCollection(Route::getRoutes());
        $contract = LaravelRouteWalker::walk($routes);
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
