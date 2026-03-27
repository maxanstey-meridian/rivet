<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ReflectCommand;

class ReflectCommandTest extends TestCase
{
    public function testRunProducesJsonFile(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/Fixtures', $tmpFile);

            $this->assertSame(0, $exitCode);
            $this->assertFileExists($tmpFile);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $this->assertArrayHasKey('types', $decoded);
            $this->assertArrayHasKey('enums', $decoded);
            $this->assertArrayHasKey('endpoints', $decoded);

            $typeNames = array_column($decoded['types'], 'name');
            $this->assertContains('ScalarDto', $typeNames);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testRunWithMissingDirReturnsError(): void
    {
        $command = new ReflectCommand();
        $this->assertSame(1, $command->run('/nonexistent', '/dev/null'));
    }

    public function testDiagnosticsWrittenToStderr(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';
        $stderrFile = sys_get_temp_dir() . '/rivet-stderr-' . uniqid() . '.txt';
        $stderr = fopen($stderrFile, 'w');

        try {
            $command = new ReflectCommand($stderr);
            $exitCode = $command->run(__DIR__ . '/Fixtures', $tmpFile);

            fclose($stderr);
            $stderrOutput = file_get_contents($stderrFile);

            $this->assertSame(0, $exitCode);
            // Fixtures/OrderItemLooseDto has untyped array → warning
            $this->assertStringContainsString('[warning]', $stderrOutput);
            $this->assertStringContainsString('OrderItemLooseDto', $stderrOutput);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
            if (file_exists($stderrFile)) {
                unlink($stderrFile);
            }
        }
    }

    public function testErrorDiagnosticsReturnExitCode1(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';
        $stderrFile = sys_get_temp_dir() . '/rivet-stderr-' . uniqid() . '.txt';
        $stderr = fopen($stderrFile, 'w');

        try {
            $command = new ReflectCommand($stderr);
            $exitCode = $command->run(__DIR__ . '/BrokenFixtures', $tmpFile);

            fclose($stderr);
            $stderrOutput = file_get_contents($stderrFile);

            $this->assertSame(1, $exitCode);
            $this->assertStringContainsString('[error]', $stderrOutput);
            $this->assertStringContainsString('NonExistentClass', $stderrOutput);
            $this->assertFileDoesNotExist($tmpFile);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
            if (file_exists($stderrFile)) {
                unlink($stderrFile);
            }
        }
    }
}
