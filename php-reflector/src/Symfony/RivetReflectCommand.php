<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Symfony;

use Rivet\PhpReflector\ClassFinder;
use Rivet\PhpReflector\ContractEmitter;
use Rivet\PhpReflector\Diagnostics;
use Rivet\PhpReflector\SymfonyRouteWalker;
use Rivet\PhpReflector\TypeCollector;
use Symfony\Component\Console\Attribute\AsCommand;
use Symfony\Component\Console\Command\Command;
use Symfony\Component\Console\Input\InputInterface;
use Symfony\Component\Console\Input\InputOption;
use Symfony\Component\Console\Output\OutputInterface;
use Symfony\Component\Routing\RouterInterface;

#[AsCommand(name: 'rivet:reflect', description: 'Generate Rivet contract JSON from Symfony routes')]
class RivetReflectCommand extends Command
{
    public function __construct(private readonly RouterInterface $router)
    {
        parent::__construct();
    }

    protected function configure(): void
    {
        $this->addOption('out', null, InputOption::VALUE_REQUIRED, 'Output file path');
        $this->addOption('dir', null, InputOption::VALUE_REQUIRED, 'Directory to scan for #[RivetType] classes');
    }

    protected function execute(InputInterface $input, OutputInterface $output): int
    {
        $routes = SymfonyRouteWalker::fromRouteCollection($this->router->getRouteCollection());

        $extraFqcns = [];
        $dir = $input->getOption('dir');
        if ($dir !== null && is_dir($dir)) {
            $allFqcns = ClassFinder::find($dir);

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

        $contract = SymfonyRouteWalker::walk($routes, $extraFqcns);

        /** @var Diagnostics $diagnostics */
        $diagnostics = $contract['diagnostics'];
        foreach ($diagnostics->formatMessages() as $line) {
            $output->writeln("<comment>$line</comment>");
        }

        if ($diagnostics->hasErrors()) {
            return Command::FAILURE;
        }

        $json = ContractEmitter::emit($contract);

        $out = $input->getOption('out');
        if ($out !== null) {
            file_put_contents($out, $json);
            $output->writeln("<info>Contract written to $out</info>");
        } else {
            $output->writeln($json);
        }

        return Command::SUCCESS;
    }
}
