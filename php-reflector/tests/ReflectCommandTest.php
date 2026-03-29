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
            $this->assertContains('SharedConfigDto', $typeNames);

            // Fixtures/ has controllers with #[RivetRoute] — verify controller path is taken
            $this->assertNotEmpty($decoded['endpoints']);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testRivetTypeClassesAppearInOutput(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/Fixtures', $tmpFile);

            $this->assertSame(0, $exitCode);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $typeNames = array_column($decoded['types'], 'name');
            $this->assertContains('SharedConfigDto', $typeNames);

            // TypeCollector returns each FQCN at most once, so no duplicates are possible.
            $this->assertSame(
                1,
                count(array_keys($typeNames, 'SharedConfigDto')),
                'SharedConfigDto must appear exactly once after deduplication'
            );
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testOutputTypeNamesAreUnique(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/Fixtures', $tmpFile);

            $this->assertSame(0, $exitCode);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $typeNames = array_column($decoded['types'], 'name');

            // TypeCollector merge + array_unique must not produce duplicates
            $this->assertSame(
                $typeNames,
                array_unique($typeNames),
                'Output type names must be unique — no duplicates from merge'
            );
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

    public function testTypesOnlyProjectProducesEmptyEndpoints(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/ReflectCommandFixtures/TaggedOnly', $tmpFile);

            $this->assertSame(0, $exitCode);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $this->assertSame([], $decoded['endpoints']);

            $typeNames = array_column($decoded['types'], 'name');
            $this->assertContains('TaggedDto', $typeNames);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testEmptyDirectoryProducesEmptyOutput(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/ReflectCommandFixtures/Empty', $tmpFile);

            $this->assertSame(0, $exitCode);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $this->assertSame([], $decoded['types']);
            $this->assertSame([], $decoded['enums']);
            $this->assertSame([], $decoded['endpoints']);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testOnlyRivetTypeClassesAppearInOutput(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/ReflectCommandFixtures/TaggedOnly', $tmpFile);

            $this->assertSame(0, $exitCode);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $typeNames = array_column($decoded['types'], 'name');
            $this->assertContains('TaggedDto', $typeNames);
            $this->assertNotContains('UntaggedDto', $typeNames);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testUntaggedClassesExcludedRegression(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/ReflectCommandFixtures/TaggedOnly', $tmpFile);

            $this->assertSame(0, $exitCode);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $typeNames = array_column($decoded['types'], 'name');
            $this->assertCount(1, $typeNames, 'Only TaggedDto should appear');
            $this->assertNotContains('UntaggedDto', $typeNames);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testTypesOnlyFallbackWhenNoControllers(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/ReflectCommandFixtures/TaggedOnly', $tmpFile);

            $this->assertSame(0, $exitCode);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $this->assertSame([], $decoded['endpoints']);

            $typeNames = array_column($decoded['types'], 'name');
            $this->assertContains('TaggedDto', $typeNames);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testControllersProduceEndpoints(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/ReflectCommandFixtures/WithControllers', $tmpFile);

            $this->assertSame(0, $exitCode);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $this->assertNotEmpty($decoded['endpoints']);

            $endpoint = $decoded['endpoints'][0];
            $this->assertSame('GET', $endpoint['httpMethod']);
            $this->assertSame('/items/{id}', $endpoint['routeTemplate']);

            $typeNames = array_column($decoded['types'], 'name');
            $this->assertContains('ResponseDto', $typeNames);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testControllerAndRivetTypeDeduplication(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/ReflectCommandFixtures/WithControllers', $tmpFile);

            $this->assertSame(0, $exitCode);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $typeNames = array_column($decoded['types'], 'name');

            // ResponseDto is both #[RivetType]-tagged and endpoint-referenced — must appear exactly once
            $this->assertSame(
                1,
                count(array_keys($typeNames, 'ResponseDto')),
                'ResponseDto must appear exactly once despite being both tagged and referenced'
            );
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testRivetTypeClassesIncludedAlongsideEndpointTypes(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';

        try {
            $command = new ReflectCommand();
            $exitCode = $command->run(__DIR__ . '/ReflectCommandFixtures/WithControllers', $tmpFile);

            $this->assertSame(0, $exitCode);

            $decoded = json_decode(file_get_contents($tmpFile), true);
            $typeNames = array_column($decoded['types'], 'name');

            // RequestDto is tagged #[RivetType] but NOT referenced by any endpoint
            $this->assertContains('RequestDto', $typeNames);
            // ResponseDto is tagged #[RivetType] AND referenced by the endpoint
            $this->assertContains('ResponseDto', $typeNames);
        } finally {
            if (file_exists($tmpFile)) {
                unlink($tmpFile);
            }
        }
    }

    public function testControllerPathErrorReturnsExitCode1(): void
    {
        $tmpFile = sys_get_temp_dir() . '/rivet-test-' . uniqid() . '.json';
        $stderrFile = sys_get_temp_dir() . '/rivet-stderr-' . uniqid() . '.txt';
        $stderr = fopen($stderrFile, 'w');

        try {
            $command = new ReflectCommand($stderr);
            $exitCode = $command->run(__DIR__ . '/ReflectCommandFixtures/BrokenController', $tmpFile);

            fclose($stderr);
            $stderrOutput = file_get_contents($stderrFile);

            $this->assertSame(1, $exitCode);
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
